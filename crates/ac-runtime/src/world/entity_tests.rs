//! The world's object directory as presentation and the object list read
//! it: a re-sent description refreshes and never spawns, the player-killer
//! bits a property update changes reach the object's view, and an object's
//! identity across delete and re-create.
//!
//! Sources: OpenAC `tests/AcDream.Core.Net.Tests/WorldSessionUpdateObjectTests.cs`,
//! `tests/AcDream.Runtime.Tests/Entities/RuntimeEntityPvpBitfieldSnapshotSyncTests.cs`,
//! `RuntimeEntityDirectoryTests.cs` and
//! `tests/AcDream.HostParity.Tests/Scenarios/WorldObjectListParityTests.cs`.

use super::tests::{create, position, stamps};
use super::*;
use ac_core::items::pk_status;
use ac_core::properties::PropertyInt;
use ac_proto::{ModelData, PublicUpdatePropertyInt, PublicWeenieDesc};

const ITEM: u32 = 0x5000_0042;
const REND_UNDERLAY: u32 = 0x0600_1234;

/// `BuildDescription`: no appearance, an empty physics block, a description
/// named `name` with an optional icon underlay.
fn description(guid: u32, name: &str, icon_underlay_id: Option<u32>) -> CreateObject {
    let Inbound::CreateObject(create) = create(guid, stamps(0, 0), None) else {
        unreachable!()
    };
    CreateObject {
        weenie: Some(PublicWeenieDesc {
            name: name.into(),
            weenie_class_id: 0x1234,
            icon_underlay_id,
            ..PublicWeenieDesc::default()
        }),
        ..*create
    }
}

fn spawns(world: &mut World) -> usize {
    world
        .drain_changes()
        .filter(|c| matches!(c, WorldChange::Spawned(_) | WorldChange::Respawned(_)))
        .count()
}

#[test]
fn update_object_refreshes_the_description_and_is_not_a_spawn() {
    // WorldSessionUpdateObjectTests.UpdateObject_RefreshesTheDescriptionAndIsNotASpawn:
    // nothing was known about the object; the description is taken, no
    // object enters the world.
    let mut world = World::new();
    world.apply(&Inbound::UpdateObject(Box::new(description(
        ITEM,
        "Sword",
        Some(REND_UNDERLAY),
    ))));
    assert_eq!(spawns(&mut world), 0);
    assert!(world.object(ITEM).is_none(), "no physics record");
    assert_eq!(world.item(ITEM).unwrap().icon_underlay_id, REND_UNDERLAY);
}

#[test]
fn create_object_stays_a_spawn_and_is_not_a_refresh() {
    // WorldSessionUpdateObjectTests.CreateObject_StaysASpawnAndIsNotARefresh.
    let mut world = World::new();
    world.apply(&Inbound::CreateObject(Box::new(description(
        ITEM, "Sword", None,
    ))));
    let changes: Vec<_> = world.drain_changes().collect();
    assert_eq!(
        changes
            .iter()
            .filter(|c| matches!(c, WorldChange::Spawned(_)))
            .count(),
        1
    );
    assert!(
        !changes
            .iter()
            .any(|c| matches!(c, WorldChange::Refreshed(_)))
    );
}

#[test]
fn update_object_republishes_the_item_with_its_new_underlay() {
    // WorldSessionUpdateObjectTests.UpdateObject_RepublishesTheItemWithItsNewUnderlay:
    // one update of the record, nothing added.
    let mut world = World::new();
    world.apply(&Inbound::CreateObject(Box::new(description(
        ITEM, "Sword", None,
    ))));
    world.drain_changes().for_each(drop);
    world.take_table_events();

    world.apply(&Inbound::UpdateObject(Box::new(description(
        ITEM,
        "Sword",
        Some(REND_UNDERLAY),
    ))));

    // The held object is created again from the description (same
    // incarnation, same identity); its item record is updated, not added.
    let key = world.object(ITEM).unwrap().key();
    let changes: Vec<_> = world.drain_changes().collect();
    assert_eq!(
        changes,
        [
            WorldChange::Respawned(key),
            WorldChange::Item(ItemChange::Updated(ITEM))
        ]
    );
    assert_eq!(world.item(ITEM).unwrap().icon_underlay_id, REND_UNDERLAY);
}

// --- A re-sent description re-creates a held object --------------------
//
// OpenAC `LiveEntityHydrationControllerTests.DescriptionRefresh_*` and
// `HeadlessSessionIsolationTests.ResentDescriptionRecreatesTheObjectWhereItNowSays`.

const HOOK: u32 = 0x7000_0050;
const NO_DRAW: u32 = 0x20;
const ETHEREAL: u32 = 0x4;

fn hook_description(instance: u16, setup_id: u32, state: u32, x: f32) -> CreateObject {
    let Inbound::CreateObject(create) =
        create(HOOK, stamps(instance, 1), Some(position(0x0101_0001, x)))
    else {
        unreachable!()
    };
    let mut desc = *create;
    desc.physics.setup_id = Some(setup_id);
    desc.physics.state = state;
    desc
}

#[test]
fn a_resent_description_recreates_the_object_with_its_new_physics_state() {
    // DescriptionRefresh_RecreatesTheObjectWithItsNewPhysicsState: a hook
    // hidden with no-draw is given an item.
    let mut world = World::new();
    world.apply(&Inbound::CreateObject(Box::new(hook_description(
        1,
        0x0200_0001,
        NO_DRAW | ETHEREAL,
        1.0,
    ))));
    world.drain_changes().for_each(drop);

    world.apply(&Inbound::UpdateObject(Box::new(hook_description(
        1,
        0x0200_0002,
        ETHEREAL,
        2.0,
    ))));

    let object = world.object(HOOK).unwrap();
    assert_eq!(object.physics().setup_id, Some(0x0200_0002));
    assert_eq!(object.physics().state & NO_DRAW, 0);
    assert_ne!(object.physics().state & ETHEREAL, 0);
    assert_eq!(object.position().unwrap().origin.x, 2.0);
    let key = object.key();
    assert!(
        world
            .drain_changes()
            .any(|c| c == WorldChange::Respawned(key))
    );
}

#[test]
fn a_resent_description_from_another_generation_is_taken() {
    // DescriptionRefresh_FromAnotherGeneration_IsTaken.
    let mut world = World::new();
    world.apply(&Inbound::CreateObject(Box::new(hook_description(
        2,
        0x0200_0001,
        0,
        1.0,
    ))));
    world.apply(&Inbound::UpdateObject(Box::new(hook_description(
        1,
        0x0200_0002,
        0,
        1.0,
    ))));

    let object = world.object(HOOK).unwrap();
    assert_eq!(object.key().incarnation, 1);
    assert_eq!(object.physics().setup_id, Some(0x0200_0002));
}

#[test]
fn a_repeated_resent_description_recreates_each_time() {
    // DescriptionRefresh_Repeated_RecreatesEachTime.
    let mut world = World::new();
    world.apply(&Inbound::CreateObject(Box::new(hook_description(
        1,
        0x0200_0001,
        0,
        1.0,
    ))));
    world.drain_changes().for_each(drop);
    for setup in [0x0200_0002, 0x0200_0003] {
        world.apply(&Inbound::UpdateObject(Box::new(hook_description(
            1, setup, 0, 1.0,
        ))));
    }

    assert_eq!(
        world.object(HOOK).unwrap().physics().setup_id,
        Some(0x0200_0003)
    );
    assert_eq!(spawns(&mut world), 2);
}

#[test]
fn a_refresh_of_an_item_with_no_physics_record_updates_the_item_only() {
    // The table holds the item (it is carried); the re-sent description
    // updates it and still puts nothing in the world.
    let mut world = World::new();
    world.apply(&Inbound::UpdateObject(Box::new(description(
        ITEM, "Sword", None,
    ))));
    world.drain_changes().for_each(drop);
    world.apply(&Inbound::UpdateObject(Box::new(description(
        ITEM,
        "Sword",
        Some(REND_UNDERLAY),
    ))));
    assert_eq!(
        world.drain_changes().collect::<Vec<_>>(),
        [WorldChange::Item(ItemChange::Updated(ITEM))]
    );
    assert!(world.object(ITEM).is_none());
}

// --- A re-created item takes the first place in its pack ----------------
//
// The game deletes a held object on a re-sent description, which takes it
// out of its container's list, and creates it again; the container has no
// place for it then and puts it first. Revealing a coalesced Aetheria's
// sigil re-sends its description, so the revealed Aetheria moves to the
// top-left slot of the pack it is in.

const AETHERIA_PACK: u32 = 0x5000_00C9;

fn pack_item(guid: u32, name: &str) -> CreateObject {
    let mut desc = description(guid, name, None);
    if let Some(weenie) = desc.weenie.as_mut() {
        weenie.container_id = Some(AETHERIA_PACK);
    }
    desc
}

fn put(world: &mut World, guid: u32, placement: u32) {
    world.apply_put_in_container(&PutObjInContainer {
        item_guid: guid,
        container_guid: AETHERIA_PACK,
        placement,
        container_type: 0,
    });
}

#[test]
fn a_resent_description_puts_a_held_item_first_in_its_pack() {
    let items = [0x5000_0101, 0x5000_0102, 0x5000_0103];
    let mut world = World::new();
    for (slot, &guid) in items.iter().enumerate() {
        world.apply(&Inbound::CreateObject(Box::new(pack_item(
            guid,
            "Coalesced Aetheria",
        ))));
        put(&mut world, guid, slot as u32);
    }
    assert_eq!(world.items().get_contents(AETHERIA_PACK), items);

    world.apply(&Inbound::UpdateObject(Box::new(pack_item(
        items[2], "Aetheria",
    ))));

    assert_eq!(
        world.items().get_contents(AETHERIA_PACK),
        [items[2], items[0], items[1]]
    );
    let revealed = world.item(items[2]).unwrap();
    assert_eq!(revealed.container_slot, 0);
    assert_eq!(revealed.name, "Aetheria");
    assert_eq!(world.item(items[0]).unwrap().container_slot, 1);
    assert_eq!(world.item(items[1]).unwrap().container_slot, 2);
}

#[test]
fn a_resent_description_of_an_item_not_held_keeps_its_place() {
    // Known only to the item table (no create arrived): the game has no
    // object to delete, and the container still lists it where it was.
    let items = [0x5000_0201, 0x5000_0202];
    let mut world = World::new();
    world.mutate_items_for_test(|table, events| {
        table.replace_contents_ids(AETHERIA_PACK, &items, events);
    });

    world.apply(&Inbound::UpdateObject(Box::new(pack_item(
        items[1], "Aetheria",
    ))));

    assert_eq!(world.items().get_contents(AETHERIA_PACK), items);
    assert!(world.object(items[1]).is_none());
}

// --- The player-killer bits in the object's view -----------------------
//
// OpenAC copies the description's bit field into each entity's snapshot
// and keeps the copy in step with the item table
// (`RuntimeEntityPvpBitfieldSnapshotSync`). Here an object's view reads the
// bit field from the item table when it is built, so there is no copy to
// fall behind; these tests pin what the sync guarantees.

const CREATURE: u32 = 0x7000_0020;
/// `BF_PLAYER` only.
const PLAYER_BIT: u32 = 0x8;

fn creature(bitfield: Option<u32>) -> World {
    let mut world = World::new();
    let message = create(CREATURE, stamps(1, 1), Some(position(0x0101_0001, 10.0)));
    let Inbound::CreateObject(mut create) = message else {
        unreachable!()
    };
    create.weenie = bitfield.map(|flags| PublicWeenieDesc {
        name: "fixture".into(),
        object_description_flags: flags,
        ..PublicWeenieDesc::default()
    });
    world.apply(&Inbound::CreateObject(create));
    world.drain_changes().for_each(drop);
    world
}

fn int_update(guid: u32, property: u32, value: i32) -> Inbound<'static> {
    Inbound::PublicUpdatePropertyInt(PublicUpdatePropertyInt {
        sequence: 0,
        guid,
        property,
        value,
    })
}

fn view_bitfield(world: &World, guid: u32) -> u32 {
    world
        .snapshot()
        .objects
        .into_iter()
        .find(|o| o.guid == guid)
        .unwrap()
        .radar
        .public_weenie_bitfield
}

#[test]
fn a_player_killer_update_rewrites_the_views_description_bits() {
    // ObjectUpdated_WithLiveBitfield_RewritesSnapshotObjectDescriptionFlags.
    let mut world = creature(Some(PLAYER_BIT));
    world.apply(&int_update(
        CREATURE,
        PropertyInt::PLAYER_KILLER_STATUS.0,
        pk_status::PK_LITE,
    ));
    assert_eq!(view_bitfield(&world, CREATURE), 0x0200_0008);
    // A window following the change stream gets the object again.
    let changes: Vec<_> = world.drain_changes().collect();
    let deltas = world.deltas(&changes);
    assert!(matches!(
        deltas.as_slice(),
        [ObjectDelta::Upsert(view)] if view.guid == CREATURE
            && view.radar.public_weenie_bitfield == 0x0200_0008
    ));
}

#[test]
fn a_hook_the_server_makes_visible_reaches_the_view_as_pickable() {
    // A hook comes hidden from the UI (0x80); the UI-hidden bool turning
    // false clears the bit in the view a window picks against, and the
    // change stream carries it.
    use ac_core::items::PublicWeenieFlags;
    use ac_core::properties::PropertyBool;
    let hidden = PublicWeenieFlags::STUCK | PublicWeenieFlags::UI_HIDDEN;
    let mut world = creature(Some(hidden.bits()));
    world.apply(&Inbound::PublicUpdatePropertyBool(
        ac_proto::PublicUpdatePropertyBool {
            sequence: 0,
            guid: CREATURE,
            property: PropertyBool::UI_HIDDEN.0,
            value: false,
        },
    ));
    assert_eq!(
        view_bitfield(&world, CREATURE),
        PublicWeenieFlags::STUCK.bits()
    );
    let changes: Vec<_> = world.drain_changes().collect();
    let deltas = world.deltas(&changes);
    assert!(matches!(
        deltas.as_slice(),
        [ObjectDelta::Upsert(view)] if view.guid == CREATURE
            && view.radar.public_weenie_bitfield == PublicWeenieFlags::STUCK.bits()
    ));
}

#[test]
fn a_public_locked_bool_update_makes_an_unlocked_chest_openable() {
    // PublicLockedBoolUpdate_MakesAnUnlockedChestOpenable: a key or a
    // lockpick unlocking a chest is the Locked bool (3) going false.
    // Everything that asks whether a container can be opened or filled, or
    // how many slots its window shows, reads the description's openable bit
    // (0x1), the inverse of Locked.
    use ac_core::items::PublicWeenieFlags;
    use ac_core::properties::PropertyBool;
    let mut world = creature(Some(PublicWeenieFlags::STUCK.bits()));
    let locked = |value| {
        Inbound::PublicUpdatePropertyBool(ac_proto::PublicUpdatePropertyBool {
            sequence: 0,
            guid: CREATURE,
            property: PropertyBool::LOCKED.0,
            value,
        })
    };
    world.apply(&locked(false));
    assert_eq!(
        view_bitfield(&world, CREATURE),
        (PublicWeenieFlags::STUCK | PublicWeenieFlags::OPENABLE).bits()
    );
    world.apply(&locked(true));
    assert_eq!(
        view_bitfield(&world, CREATURE),
        PublicWeenieFlags::STUCK.bits()
    );
}

#[test]
fn an_unrelated_property_leaves_the_description_bits() {
    // ObjectUpdated_UnrelatedProperty_DoesNotRewriteSnapshot (UI effects, 18).
    let mut world = creature(Some(PLAYER_BIT));
    world.apply(&int_update(CREATURE, PropertyInt::UI_EFFECTS.0, 4));
    assert_eq!(view_bitfield(&world, CREATURE), PLAYER_BIT);
}

#[test]
fn no_description_bits_yet_reads_none() {
    // ObjectUpdated_NoPublicWeenieBitfieldYet_NoOp: the object came with no
    // description, so there is no record to change and the view reads 0.
    let mut world = creature(None);
    world.apply(&int_update(CREATURE, PropertyInt::UI_EFFECTS.0, 1));
    assert_eq!(view_bitfield(&world, CREATURE), 0);
    assert!(
        world
            .item(CREATURE)
            .unwrap()
            .public_weenie_bitfield
            .is_none()
    );
}

#[test]
fn an_item_with_no_object_in_the_world_changes_no_view() {
    // ObjectUpdated_NoMatchingActiveEntity_NoOp: an inventory item.
    const PACK_ITEM: u32 = 0x8000_0001;
    let mut world = World::new();
    world.apply(&Inbound::UpdateObject(Box::new(CreateObject {
        weenie: Some(PublicWeenieDesc {
            object_description_flags: PLAYER_BIT,
            ..PublicWeenieDesc::default()
        }),
        ..description(PACK_ITEM, "pack item", None)
    })));
    world.drain_changes().for_each(drop);
    world.apply(&int_update(
        PACK_ITEM,
        PropertyInt::PLAYER_KILLER_STATUS.0,
        pk_status::PK,
    ));
    let changes: Vec<_> = world.drain_changes().collect();
    assert!(world.deltas(&changes).is_empty());
    assert_eq!(world.object_count(), 0);
}

#[test]
fn an_appearance_change_after_a_player_killer_update_keeps_the_live_bits() {
    // ObjDescAfterPkUpdate_MergedSnapshotKeepsLiveBitfield.
    let mut world = creature(Some(PLAYER_BIT));
    world.apply(&int_update(
        CREATURE,
        PropertyInt::PLAYER_KILLER_STATUS.0,
        pk_status::PK_LITE,
    ));
    world.apply(&Inbound::ObjDescEvent(ObjDescEvent {
        guid: CREATURE,
        model: ModelData {
            base_palette_id: Some(0x0400_0001),
            ..ModelData::default()
        },
        instance_sequence: 1,
        obj_desc_sequence: 1,
    }));
    let changes: Vec<_> = world.drain_changes().collect();
    assert!(changes.contains(&WorldChange::AppearanceChanged(
        world.object(CREATURE).unwrap().key()
    )));
    assert_eq!(view_bitfield(&world, CREATURE), 0x0200_0008);
}

// --- Identity -----------------------------------------------------------

const NEWCOMER: u32 = 0x5000_0031;

#[test]
fn one_current_incarnation_per_guid() {
    // RuntimeEntityDirectoryTests.AddActive_OwnsOneCurrentIncarnationPerGuid:
    // a second create of a newer incarnation replaces the first; the guid
    // never has two objects.
    let mut world = World::new();
    world.apply(&create(
        NEWCOMER,
        stamps(1, 1),
        Some(position(0x0101_0001, 1.0)),
    ));
    world.apply(&create(
        NEWCOMER,
        stamps(2, 1),
        Some(position(0x0101_0001, 2.0)),
    ));
    assert_eq!(world.object_count(), 1);
    assert_eq!(world.object(NEWCOMER).unwrap().key().incarnation, 2);
}

#[test]
fn create_delete_and_create_again_reads_the_same_object() {
    // WorldObjectListParityTests.CreateDeleteAndCreateAgainReadTheSameOnBothClients:
    // the object leaves the list on delete, comes back on the next create,
    // and each appearance is announced once.
    let mut world = World::new();
    world.apply(&create(
        NEWCOMER,
        stamps(1, 1),
        Some(position(0x0101_0001, 5.0)),
    ));
    let first = world.object(NEWCOMER).unwrap().key();
    assert_eq!(spawns(&mut world), 1);

    world.apply(&Inbound::DeleteObject(ac_proto::DeleteObject {
        guid: NEWCOMER,
        instance_sequence: 1,
    }));
    assert!(world.object(NEWCOMER).is_none());
    assert!(!world.snapshot().objects.iter().any(|o| o.guid == NEWCOMER));
    assert_eq!(spawns(&mut world), 0);

    world.apply(&create(
        NEWCOMER,
        stamps(2, 1),
        Some(position(0x0101_0001, 6.0)),
    ));
    let again = world.object(NEWCOMER).unwrap().key();
    assert_eq!(spawns(&mut world), 1);
    assert!(world.snapshot().objects.iter().any(|o| o.key == again));
    // The local id is kept across incarnations (OpenAC
    // claims a new one), the incarnation tells them apart.
    assert_eq!(again.local_id, first.local_id);
    assert_ne!(again, first);
    assert_eq!(
        world.object_by_local_id(first.local_id).unwrap().key(),
        again
    );
}

#[test]
fn an_item_going_into_a_pack_leaves_the_world_but_keeps_its_record() {
    // WorldObjectListParityTests.AnItemGoingIntoAPackLeavesTheListOnBothClients.
    const GROUND_ITEM: u32 = 0x5000_0032;
    const PLAYER: u32 = 0x5000_000A;
    let mut world = World::new();
    world.set_player(PLAYER);
    let Inbound::CreateObject(mut item) =
        create(GROUND_ITEM, stamps(1, 1), Some(position(0x0101_0001, 1.0)))
    else {
        unreachable!()
    };
    item.weenie = Some(PublicWeenieDesc {
        name: "ground item".into(),
        ..PublicWeenieDesc::default()
    });
    world.apply(&Inbound::CreateObject(item));
    assert!(
        world
            .snapshot()
            .objects
            .iter()
            .any(|o| o.guid == GROUND_ITEM)
    );

    // The server takes it out of the world and the record names the pack.
    world.apply(&Inbound::DeleteObject(ac_proto::DeleteObject {
        guid: GROUND_ITEM,
        instance_sequence: 1,
    }));
    world.apply(&Inbound::UpdateObject(Box::new(CreateObject {
        weenie: Some(PublicWeenieDesc {
            name: "ground item".into(),
            container_id: Some(PLAYER),
            ..PublicWeenieDesc::default()
        }),
        ..description(GROUND_ITEM, "ground item", None)
    })));
    assert!(
        !world
            .snapshot()
            .objects
            .iter()
            .any(|o| o.guid == GROUND_ITEM)
    );
    assert_eq!(world.item(GROUND_ITEM).unwrap().container_id, PLAYER);
}

#[test]
fn a_late_watcher_is_shown_every_object_once() {
    // WorldObjectListParityTests.ALateWatcherIsReplayedTheSameWorldOnBothClients:
    // a window attaching late gets the whole population once, then hears
    // about the newcomer.
    let mut world = World::new();
    for (guid, x) in [(0x7000_0001, 1.0), (0x7000_0002, 2.0), (0x7000_0003, 3.0)] {
        world.apply(&create(guid, stamps(1, 1), Some(position(0x0101_0001, x))));
    }
    world.drain_changes().for_each(drop);
    let snapshot = world.snapshot();
    let mut ids: Vec<u32> = snapshot.objects.iter().map(|o| o.key.local_id).collect();
    ids.sort_unstable();
    ids.dedup();
    assert_eq!(ids.len(), 3);

    world.apply(&create(
        NEWCOMER,
        stamps(1, 1),
        Some(position(0x0101_0001, 5.0)),
    ));
    let changes: Vec<_> = world.drain_changes().collect();
    let deltas = world.deltas(&changes);
    assert!(matches!(
        deltas.as_slice(),
        [ObjectDelta::Upsert(view)] if view.guid == NEWCOMER
    ));
}
