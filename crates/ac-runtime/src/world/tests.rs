//! World application rules. Scenarios follow OpenAC
//! `tests/AcDream.Runtime.Tests/Entities/InboundPhysicsStateControllerTests.cs`
//! and `tests/AcDream.Core.Net.Tests/ObjectTableWiringTests.cs`; the timestamp
//! rules themselves are pinned in `ac_core::physics::timestamps`.

use super::*;
use ac_proto::{
    ModelData, PhysicsDesc, PhysicsTimestamps as WireStamps, Position, PrivateUpdateVital,
    PublicUpdatePropertyInt, Quaternion,
};

const PLAYER: u32 = 0x5000_000A;
const DOOR: u32 = 0x7000_0001;

pub(crate) fn stamps(instance: u16, position: u16) -> WireStamps {
    WireStamps {
        instance,
        position,
        ..WireStamps::default()
    }
}

pub(crate) fn position(cell: u32, x: f32) -> Position {
    Position {
        cell_id: cell,
        origin: Vector3 { x, y: 10.0, z: 0.0 },
        orientation: Quaternion::IDENTITY,
    }
}

pub(crate) fn physics(ts: WireStamps, at: Option<Position>) -> PhysicsDesc {
    PhysicsDesc {
        flags: 0,
        state: 0,
        movement: None,
        animation_frame: None,
        position: at,
        motion_table_id: None,
        sound_table_id: None,
        physics_script_table_id: None,
        setup_id: Some(0x0200_0001),
        parent: None,
        children: None,
        scale: None,
        friction: None,
        elasticity: None,
        translucency: None,
        velocity: None,
        acceleration: None,
        omega: None,
        default_script: None,
        default_script_intensity: None,
        timestamps: ts,
    }
}

pub(crate) fn create(guid: u32, ts: WireStamps, at: Option<Position>) -> Inbound<'static> {
    Inbound::CreateObject(Box::new(CreateObject {
        guid,
        model: ModelData::default(),
        physics: physics(ts, at),
        weenie: None,
    }))
}

fn moved(guid: u32, instance: u16, position_seq: u16, x: f32) -> Inbound<'static> {
    Inbound::UpdatePosition(UpdatePosition {
        guid,
        flags: 0,
        position: position(0xA9B4_0031, x),
        velocity: None,
        placement_id: None,
        instance_sequence: instance,
        position_sequence: position_seq,
        teleport_sequence: 0,
        force_position_sequence: 0,
    })
}

fn world_with_door() -> World {
    let mut world = World::new();
    world.apply(&create(
        DOOR,
        stamps(1, 10),
        Some(position(0xA9B4_0031, 5.0)),
    ));
    world.drain_changes().for_each(drop);
    world.take_table_events();
    world
}

/// The physics changes recorded since the last call; item-table changes
/// are checked by their own tests.
fn physics_changes(world: &mut World) -> Vec<WorldChange> {
    world
        .drain_changes()
        .filter(|c| !matches!(c, WorldChange::Item(_)))
        .collect()
}

#[test]
fn create_spawns_once_and_keeps_its_local_id_across_incarnations() {
    let mut world = World::new();
    world.apply(&create(
        DOOR,
        stamps(1, 10),
        Some(position(0xA9B4_0031, 5.0)),
    ));
    let first = world.object(DOOR).unwrap().key();
    assert_eq!(physics_changes(&mut world), [WorldChange::Spawned(first)]);

    // Same incarnation: refreshed, not re-spawned.
    world.apply(&create(
        DOOR,
        stamps(1, 10),
        Some(position(0xA9B4_0031, 5.0)),
    ));
    assert_eq!(
        physics_changes(&mut world).first(),
        Some(&WorldChange::Refreshed(first))
    );

    // Newer incarnation: re-created with the same local id.
    world.apply(&create(
        DOOR,
        stamps(2, 1),
        Some(position(0xA9B4_0031, 6.0)),
    ));
    let second = world.object(DOOR).unwrap().key();
    assert_eq!(second.local_id, first.local_id);
    assert_eq!(second.incarnation, 2);
    assert_eq!(
        physics_changes(&mut world).first(),
        Some(&WorldChange::Respawned(second))
    );

    // Older incarnation: ignored.
    world.apply(&create(
        DOOR,
        stamps(1, 99),
        Some(position(0xA9B4_0031, 9.0)),
    ));
    assert_eq!(physics_changes(&mut world).len(), 0);
    assert_eq!(
        world.object(DOOR).unwrap().position().unwrap().origin.x,
        6.0
    );
}

#[test]
fn positions_apply_only_when_newer() {
    let mut world = world_with_door();
    world.apply(&moved(DOOR, 1, 11, 7.0));
    assert_eq!(
        world.object(DOOR).unwrap().position().unwrap().origin.x,
        7.0
    );
    world.apply(&moved(DOOR, 1, 11, 8.0)); // duplicate
    world.apply(&moved(DOOR, 1, 10, 8.0)); // straggler
    world.apply(&moved(DOOR, 2, 12, 8.0)); // wrong incarnation
    assert_eq!(
        world.object(DOOR).unwrap().position().unwrap().origin.x,
        7.0
    );
    assert_eq!(physics_changes(&mut world).len(), 1);
}

#[test]
fn remote_moves_take_the_sent_velocity_or_zero() {
    let mut world = world_with_door();
    world.apply(&moved(DOOR, 1, 11, 7.0));
    assert_eq!(
        world.object(DOOR).unwrap().physics().velocity,
        Some(Vector3::default())
    );
}

#[test]
fn delete_needs_the_exact_incarnation_and_never_removes_the_player() {
    let mut world = world_with_door();
    world.apply(&Inbound::DeleteObject(DeleteObject {
        guid: DOOR,
        instance_sequence: 2,
    }));
    assert!(world.object(DOOR).is_some());
    world.apply(&Inbound::DeleteObject(DeleteObject {
        guid: DOOR,
        instance_sequence: 1,
    }));
    assert!(world.object(DOOR).is_none());

    world.set_player(PLAYER);
    world.apply(&create(
        PLAYER,
        stamps(1, 1),
        Some(position(0xA9B4_0031, 1.0)),
    ));
    world.apply(&Inbound::DeleteObject(DeleteObject {
        guid: PLAYER,
        instance_sequence: 1,
    }));
    assert!(world.object(PLAYER).is_some());
}

#[test]
fn pickup_and_parent_share_the_position_counter() {
    let mut world = world_with_door();
    world.apply(&create(
        PLAYER,
        stamps(3, 1),
        Some(position(0xA9B4_0031, 1.0)),
    ));
    world.apply(&Inbound::ParentEvent(ParentEvent {
        parent_guid: PLAYER,
        child_guid: DOOR,
        parent_location: 1,
        placement_id: 0x65,
        parent_instance_sequence: 3,
        child_position_sequence: 11,
    }));
    let door = world.object(DOOR).unwrap();
    assert_eq!(door.parent().map(|p| p.guid), Some(PLAYER));
    assert!(door.position().is_none());

    // A pickup with the same counter is stale; a newer one lands.
    world.apply(&Inbound::PickupEvent(PickupEvent {
        guid: DOOR,
        instance_sequence: 1,
        position_sequence: 11,
    }));
    assert!(world.object(DOOR).unwrap().parent().is_some());
    world.apply(&Inbound::PickupEvent(PickupEvent {
        guid: DOOR,
        instance_sequence: 1,
        position_sequence: 12,
    }));
    assert!(world.object(DOOR).unwrap().parent().is_none());
}

#[test]
fn parent_event_needs_the_parents_current_incarnation() {
    let mut world = world_with_door();
    world.apply(&create(
        PLAYER,
        stamps(3, 1),
        Some(position(0xA9B4_0031, 1.0)),
    ));
    world.apply(&Inbound::ParentEvent(ParentEvent {
        parent_guid: PLAYER,
        child_guid: DOOR,
        parent_location: 1,
        placement_id: 0x65,
        parent_instance_sequence: 2,
        child_position_sequence: 11,
    }));
    assert!(world.object(DOOR).unwrap().parent().is_none());
}

#[test]
fn state_and_vector_updates_are_gated_independently() {
    let mut world = world_with_door();
    world.apply(&Inbound::SetState(SetState {
        guid: DOOR,
        physics_state: 0x0400,
        instance_sequence: 1,
        state_sequence: 1,
    }));
    world.apply(&Inbound::SetState(SetState {
        guid: DOOR,
        physics_state: 0x0001,
        instance_sequence: 1,
        state_sequence: 1,
    }));
    assert_eq!(world.object(DOOR).unwrap().physics().state, 0x0400);
    world.apply(&Inbound::VectorUpdate(VectorUpdate {
        guid: DOOR,
        velocity: Vector3 {
            x: 1.0,
            y: 0.0,
            z: 0.0,
        },
        omega: Vector3::default(),
        instance_sequence: 1,
        vector_sequence: 1,
    }));
    assert_eq!(
        world.object(DOOR).unwrap().physics().velocity.map(|v| v.x),
        Some(1.0)
    );
}

#[test]
fn public_property_updates_land_on_the_item_record() {
    // OpenAC ObjectTableWiringTests: every public int update is applied, and
    // a stack-size answer sets the size and value fields.
    let mut world = world_with_door();
    world.apply(&Inbound::PublicUpdatePropertyInt(PublicUpdatePropertyInt {
        sequence: 0,
        guid: DOOR,
        property: 18,
        value: 7,
    }));
    assert_eq!(world.item(DOOR).unwrap().properties.ints.get(&18), Some(&7));
    world.apply(&Inbound::SetStackSize(ac_proto::SetStackSize {
        sequence: 0,
        guid: DOOR,
        stack_size: 25,
        value: 500,
    }));
    let door = world.item(DOOR).unwrap();
    assert_eq!((door.stack_size, door.value), (25, 500));
    let events = world.take_table_events();
    assert!(events.iter().any(
        |e| matches!(e, TableEvent::StackSizeUpdated { object_id, .. } if *object_id == DOOR)
    ));
}

#[test]
fn private_updates_wait_for_the_player_description() {
    let mut world = World::new();
    world.set_player(PLAYER);
    let vital = Inbound::PrivateUpdateVital(PrivateUpdateVital {
        sequence: 0,
        vital: 2,
        ranks: 10,
        start: 100,
        xp: 5,
        current: 80,
    });
    world.apply(&vital);
    assert!(world.player().is_none());
    world.apply_player_description(&empty_description());
    world.apply(&vital);
    let health = world
        .player()
        .unwrap()
        .numbers()
        .get(ac_core::player::VitalKind::Health)
        .unwrap();
    assert_eq!((health.ranks, health.current), (10, 80));
}

#[test]
fn a_public_string_update_stores_the_name_but_keeps_the_shown_name() {
    // OpenAC WorldSessionUpdateObjectTests
    // .PublicStringUpdate_StoresTheNameButKeepsTheShownName: the value lands
    // in the property table only; the shown name changes only when the
    // object is created or updated again. The object is republished.
    let mut world = World::new();
    world.apply(&named(DOOR, 1, "Crystal"));
    world.take_table_events();
    world.apply(&Inbound::PublicUpdatePropertyString(
        ac_proto::PublicUpdatePropertyString {
            sequence: 1,
            guid: DOOR,
            property: 1,
            value: "RabidDee's Cottage".into(),
        },
    ));
    let door = world.item(DOOR).unwrap();
    assert_eq!(door.name, "Crystal");
    assert_eq!(
        door.properties.strings.get(&1).map(String::as_str),
        Some("RabidDee's Cottage")
    );
    let updated: Vec<u32> = world
        .take_table_events()
        .into_iter()
        .filter_map(|e| match e {
            TableEvent::Updated { object_id, .. } => Some(object_id),
            _ => None,
        })
        .collect();
    assert_eq!(updated, [DOOR]);

    // PublicStringUpdate_OtherProperty_LeavesNameAlone.
    world.apply(&Inbound::PublicUpdatePropertyString(
        ac_proto::PublicUpdatePropertyString {
            sequence: 2,
            guid: DOOR,
            property: 16,
            value: "A note".into(),
        },
    ));
    let door = world.item(DOOR).unwrap();
    assert_eq!(door.name, "Crystal");
    assert_eq!(
        door.properties.strings.get(&16).map(String::as_str),
        Some("A note")
    );
}

#[test]
fn a_private_string_update_keeps_the_players_shown_name() {
    // OpenAC WorldSessionUpdateObjectTests
    // .PrivateStringUpdate_StoresTheNameButKeepsThePlayersShownName.
    let mut world = World::new();
    world.set_player(PLAYER);
    world.apply(&named(PLAYER, 1, "Player"));
    world.apply(&Inbound::PrivateUpdatePropertyString(
        ac_proto::PrivateUpdatePropertyString {
            sequence: 1,
            property: 1,
            value: "Renamed".into(),
        },
    ));
    let player = world.item(PLAYER).unwrap();
    assert_eq!(player.name, "Player");
    assert_eq!(
        player.properties.strings.get(&1).map(String::as_str),
        Some("Renamed")
    );
}

#[test]
fn a_private_int_update_reaches_both_player_projections() {
    // OpenAC ObjectTableWiringTests.ApplyPlayerInt64PropertyUpdate_*: the
    // player's value lives in the item table and in the local player's
    // numbers; one message updates both.
    let mut world = World::new();
    world.set_player(PLAYER);
    world.apply(&create(
        PLAYER,
        stamps(1, 1),
        Some(position(0xA9B4_0031, 1.0)),
    ));
    world.apply_player_description(&empty_description());
    world.apply(&Inbound::PrivateUpdatePropertyInt(
        ac_proto::PrivateUpdatePropertyInt {
            sequence: 0,
            property: 5,
            value: 75,
        },
    ));
    assert_eq!(
        world.item(PLAYER).unwrap().properties.ints.get(&5),
        Some(&75)
    );
    assert_eq!(
        world.player().unwrap().numbers().int_properties().get(&5),
        Some(&75)
    );
    world.apply(&Inbound::PrivateUpdatePropertyInt64(
        ac_proto::PrivateUpdatePropertyInt64 {
            sequence: 0,
            property: 2,
            value: 75_000,
        },
    ));
    assert_eq!(
        world.item(PLAYER).unwrap().properties.int64s.get(&2),
        Some(&75_000)
    );
}

#[test]
fn delete_evicts_the_item_record() {
    // OpenAC ObjectTableWiringTests.EntityDeleted_EvictsWeenieFromTable.
    let mut world = world_with_door();
    assert!(world.item(DOOR).is_some());
    world.apply(&Inbound::DeleteObject(DeleteObject {
        guid: DOOR,
        instance_sequence: 1,
    }));
    assert!(world.item(DOOR).is_none());
    assert!(world.take_table_events().iter().any(|e| matches!(
        e,
        TableEvent::Removed {
            reason: ac_core::items::RemovalReason::LogicalDelete,
            ..
        }
    )));
}

pub(crate) fn named(guid: u32, instance: u16, name: &str) -> Inbound<'static> {
    let mut message = create(guid, stamps(instance, 1), None);
    if let Inbound::CreateObject(create) = &mut message {
        create.weenie = Some(ac_proto::PublicWeenieDesc {
            name: name.into(),
            ..ac_proto::PublicWeenieDesc::default()
        });
    }
    message
}

#[test]
fn a_new_generation_replaces_the_prior_qualities() {
    // OpenAC ObjectTableWiringTests.NewGenerationCreateObject_ReplacesPriorQualities.
    let mut world = World::new();
    world.apply(&named(DOOR, 1, "Old"));
    world.apply(&Inbound::PublicUpdatePropertyInt(PublicUpdatePropertyInt {
        sequence: 0,
        guid: DOOR,
        property: 123,
        value: 456,
    }));
    world.take_table_events();
    world.apply(&named(DOOR, 2, "Stale Name"));
    let door = world.item(DOOR).unwrap();
    assert_eq!(door.name, "Stale Name");
    assert!(!door.properties.ints.contains_key(&123));
    let removals: Vec<_> = world
        .take_table_events()
        .into_iter()
        .filter_map(|e| match e {
            TableEvent::Removed { reason, .. } => Some(reason),
            _ => None,
        })
        .collect();
    assert_eq!(
        removals,
        [ac_core::items::RemovalReason::GenerationReplacement]
    );
}

#[test]
fn a_same_generation_create_merges_the_prior_qualities() {
    // OpenAC ObjectTableWiringTests.SameGenerationCreateObject_MergesPriorQualities.
    let mut world = World::new();
    world.apply(&named(DOOR, 1, "Old"));
    world.apply(&Inbound::PublicUpdatePropertyInt(PublicUpdatePropertyInt {
        sequence: 0,
        guid: DOOR,
        property: 123,
        value: 456,
    }));
    world.apply(&named(DOOR, 1, "Refreshed Name"));
    let door = world.item(DOOR).unwrap();
    assert_eq!(door.name, "Refreshed Name");
    assert_eq!(door.properties.ints.get(&123), Some(&456));
}

#[test]
fn inventory_remove_drops_the_item_record_but_keeps_the_physics_record() {
    // OpenAC ObjectTableWiring: InventoryRemoveObject removes only the item
    // record; the physics entity waits for its delete.
    let mut world = world_with_door();
    world.apply(&Inbound::InventoryRemoveObject(
        ac_proto::InventoryRemoveObject { guid: DOOR },
    ));
    assert!(world.item(DOOR).is_none());
    assert!(world.object(DOOR).is_some());
}

#[test]
fn the_description_fills_the_inventory_and_equipment_lists() {
    // OpenAC GameEventWiring PlayerDescription: InitializeInventoryManifest
    // and InitializeEquipmentManifest under the player.
    const PACK: u32 = 0x6000_0001;
    const GEM: u32 = 0x6000_0002;
    const SWORD: u32 = 0x6000_0003;
    let mut world = World::new();
    world.set_player(PLAYER);
    let mut description = empty_description();
    description.inventory = vec![
        ac_proto::InventoryEntry {
            guid: PACK,
            container_type: 1,
        },
        ac_proto::InventoryEntry {
            guid: GEM,
            container_type: 0,
        },
    ];
    description.equipped = vec![ac_proto::EquippedEntry {
        guid: SWORD,
        equip_location: 0x0010_0000,
        priority: 0,
    }];
    world.apply_player_description(&description);
    assert_eq!(world.items().get_contents(PLAYER), [PACK, GEM]);
    assert_eq!(world.item(PACK).unwrap().container_type_hint, 1);
    let equipped: Vec<u32> = world
        .items()
        .get_equipped_by(PLAYER)
        .iter()
        .map(|i| i.object_id())
        .collect();
    assert_eq!(equipped, [SWORD]);
}

/// A game event body for the player: header, then the payload words.
pub(crate) fn game_event(event_type: GameEventType, payload: &[u32]) -> Vec<u8> {
    let mut body = Vec::new();
    for word in [ac_proto::Opcode::GameEvent.value(), PLAYER, 1, event_type.0]
        .iter()
        .chain(payload)
    {
        body.extend_from_slice(&word.to_le_bytes());
    }
    body
}

#[test]
fn server_moves_wields_and_refusals_reach_the_table() {
    const ITEM: u32 = 0x6000_0010;
    const PACK: u32 = 0x6000_0011;
    let mut world = World::new();
    world.set_player(PLAYER);
    world.apply(&named(ITEM, 1, "Gem"));
    world.apply(&named(PACK, 1, "Pack"));
    world.take_table_events();
    let put = game_event(
        GameEventType::INVENTORY_PUT_OBJ_IN_CONTAINER,
        &[ITEM, PACK, 0, 0],
    );
    world.apply(&Inbound::parse(&put).unwrap());
    assert_eq!(world.item(ITEM).unwrap().container_id, PACK);
    let wield = game_event(GameEventType::WIELD_OBJECT, &[ITEM, 0x0010_0000]);
    world.apply(&Inbound::parse(&wield).unwrap());
    let item = world.item(ITEM).unwrap();
    assert_eq!((item.wielder_id, item.container_id), (PLAYER, 0));
    let refused = game_event(GameEventType::INVENTORY_SERVER_SAVE_FAILED, &[ITEM, 0x1D]);
    world.apply(&Inbound::parse(&refused).unwrap());
    let events = world.take_table_events();
    assert!(
        events
            .iter()
            .any(|e| matches!(e, TableEvent::WieldConfirmed(id) if *id == ITEM))
    );
    assert!(events.iter().any(|e| matches!(
        e,
        TableEvent::MoveRequestFailed(f) if f.item_id == ITEM && f.weenie_error == 0x1D
    )));
    let view = game_event(GameEventType::VIEW_CONTENTS, &[PACK, 1, ITEM, 0]);
    world.apply(&Inbound::parse(&view).unwrap());
    assert_eq!(world.items().get_contents(PACK), [ITEM]);
    let closed = game_event(GameEventType::CLOSE_GROUND_CONTAINER, &[PACK]);
    world.apply(&Inbound::parse(&closed).unwrap());
    assert!(world.items().get_contents(PACK).is_empty());
}

#[test]
fn put_in_3d_takes_the_item_out_and_confirms_an_optimistic_drop() {
    // OpenAC GameEventWiringTests.WireAll_InventoryPutObjectIn3D_UnparentsFromContainer
    // and _ConfirmsOptimisticDrop.
    const DROPPED: u32 = 0x5000_0A01;
    const OPTIMISTIC: u32 = 0x5000_0A03;
    const PACK: u32 = 0x5000_00C9;
    let mut world = World::new();
    world.set_player(PLAYER);
    world.apply(&named(DROPPED, 1, "Gem"));
    world.apply(&named(OPTIMISTIC, 1, "Gem"));
    world.with_items(|table, events| {
        table.record_membership(
            DROPPED,
            PACK,
            ac_core::items::EquipMask::NONE,
            None,
            None,
            events,
        );
        table.record_membership(
            OPTIMISTIC,
            PACK,
            ac_core::items::EquipMask::NONE,
            None,
            None,
            events,
        );
        table.move_item(
            OPTIMISTIC,
            PACK,
            4,
            ac_core::items::EquipMask::NONE,
            None,
            events,
        );
        table.move_item_optimistic(OPTIMISTIC, 0, -1, events);
    });
    for item in [DROPPED, OPTIMISTIC] {
        let put = game_event(GameEventType::INVENTORY_PUT_OBJECT_IN_3D, &[item]);
        world.apply(&Inbound::parse(&put).unwrap());
        assert_eq!(world.item(item).unwrap().container_id, 0);
    }
    // Confirmed: nothing left to roll back.
    assert!(!world.with_items(|table, events| table.rollback_move(OPTIMISTIC, events)));
}

pub(crate) fn empty_description() -> PlayerDescription {
    PlayerDescription {
        property_flags: ac_proto::DescriptionPropertyFlags(0),
        weenie_type: 0,
        properties: ac_proto::PropertyTables::default(),
        positions: BTreeMap::new(),
        vector_flags: ac_proto::DescriptionVectorFlags(0),
        has_health: false,
        attributes: Vec::new(),
        skills: Vec::new(),
        spells: BTreeMap::new(),
        enchantments: Vec::new(),
        option_flags: ac_proto::CharacterOptionDataFlags(0),
        options1: 0,
        options2: 0,
        shortcuts: Vec::new(),
        hotbar_spells: Vec::new(),
        desired_comps: Vec::new(),
        spellbook_filters: 0,
        gameplay_options: Vec::new(),
        inventory: Vec::new(),
        equipped: Vec::new(),
        trailer_truncated: false,
    }
}

#[test]
fn an_invalid_position_is_counted_but_does_not_move_the_object() {
    // OpenAC RuntimeAuthoritativePositionRouteClassifier: a non-finite or
    // invalid frame is rejected after the timestamp gate accepted it.
    let mut world = world_with_door();
    let mut bad = moved(DOOR, 1, 11, f32::NAN);
    if let Inbound::UpdatePosition(u) = &mut bad {
        u.position.origin.x = f32::NAN;
    }
    world.apply(&bad);
    assert_eq!(
        world.object(DOOR).unwrap().position().unwrap().origin.x,
        5.0
    );
    assert_eq!(world.object(DOOR).unwrap().timestamps().position, 11);
    world.apply(&moved(DOOR, 1, 11, 7.0));
    assert_eq!(
        world.object(DOOR).unwrap().position().unwrap().origin.x,
        5.0
    );
}

#[test]
fn snapshot_lists_every_object_with_its_setup_and_place() {
    let world = world_with_door();
    let snapshot = world.snapshot();
    assert_eq!(snapshot.objects.len(), 1);
    let door = &snapshot.objects[0];
    assert_eq!(door.guid, DOOR);
    assert_eq!(door.setup_id, Some(0x0200_0001));
    assert_eq!(door.position.map(|p| p.origin.x), Some(5.0));
}

#[test]
fn a_view_carries_what_the_radar_reads_from_the_description() {
    // OpenAC `RadarSnapshotProvider`: the radar behaviour, item type, blip
    // colour and public description bits come from the object's description.
    let mut world = World::new();
    world.apply(&Inbound::CreateObject(Box::new(CreateObject {
        guid: DOOR,
        model: ModelData::default(),
        physics: physics(stamps(1, 10), Some(position(0xA9B4_0031, 5.0))),
        weenie: Some(ac_proto::PublicWeenieDesc {
            name: "Drudge".into(),
            item_type: 0x10,
            radar_blip_color: Some(4),
            radar_behavior: Some(3),
            object_description_flags: 0x0000_0010,
            monarch_id: Some(0x5000_0777),
            ..ac_proto::PublicWeenieDesc::default()
        }),
    })));
    let radar = world.snapshot().objects[0].radar;
    assert_eq!(
        radar,
        super::RadarFacts {
            item_type: 0x10,
            public_weenie_bitfield: 0x10,
            radar_behavior: Some(3),
            radar_blip_color: 4,
            monarch_id: 0x5000_0777,
        }
    );
    // An undescribed object reads zero.
    let bare = world_with_door().snapshot().objects[0].radar;
    assert_eq!(bare, super::RadarFacts::default());
}

#[test]
fn a_monarch_update_reaches_the_radar_facts_as_a_delta() {
    // Swearing allegiance announces the monarch with an instance-id update
    // (Monarch, 26); breaking it sends 0. The window learns it from the
    // object's delta.
    let mut world = world_with_door();
    world.drain_changes();
    let monarch_update = |value| {
        Inbound::PublicUpdatePropertyInstanceId(ac_proto::PublicUpdatePropertyInstanceId {
            sequence: 0,
            guid: DOOR,
            property: 26,
            value,
        })
    };
    let radar_of = |world: &mut World| {
        let changes: Vec<_> = world.drain_changes().collect();
        match world.deltas(&changes).as_slice() {
            [ObjectDelta::Upsert(view)] => view.radar,
            other => panic!("expected the door's delta, got {other:?}"),
        }
    };
    world.apply(&monarch_update(0x5000_0777));
    assert_eq!(radar_of(&mut world).monarch_id, 0x5000_0777);
    world.apply(&monarch_update(0));
    assert_eq!(radar_of(&mut world).monarch_id, 0);
}

#[test]
fn deltas_coalesce_to_the_latest_state_per_object() {
    let mut world = world_with_door();
    world.apply(&moved(DOOR, 1, 11, 7.0));
    world.apply(&moved(DOOR, 1, 12, 8.0));
    world.apply(&create(
        PLAYER,
        stamps(1, 1),
        Some(position(0xA9B4_0031, 1.0)),
    ));
    let changes: Vec<_> = world.drain_changes().collect();
    // Two moves, the spawn, and the new object's item record.
    assert_eq!(changes.len(), 4);
    let deltas = world.deltas(&changes);
    assert_eq!(deltas.len(), 2);
    match &deltas[0] {
        ObjectDelta::Upsert(view) => {
            assert_eq!(view.guid, DOOR);
            assert_eq!(view.position.map(|p| p.origin.x), Some(8.0));
        }
        other => panic!("expected the door, got {other:?}"),
    }

    world.apply(&Inbound::DeleteObject(DeleteObject {
        guid: DOOR,
        instance_sequence: 1,
    }));
    let changes: Vec<_> = world.drain_changes().collect();
    let key = world_with_door().object(DOOR).unwrap().key();
    assert_eq!(world.deltas(&changes), [ObjectDelta::Remove(key)]);
}

fn walking(guid: u32, movement_seq: u16, action_stamp: u16) -> Inbound<'static> {
    Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid,
        instance_sequence: 1,
        movement_sequence: movement_seq,
        server_control_sequence: 0,
        is_autonomous: false,
        movement: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: 0x3D,
            movement: ac_proto::Movement::Interpreted {
                state: ac_proto::InterpretedMotionState {
                    packed_flags: 0,
                    current_style: None,
                    forward_command: Some(0x0005),
                    sidestep_command: None,
                    turn_command: None,
                    forward_speed: Some(1.5),
                    sidestep_speed: None,
                    turn_speed: None,
                    commands: vec![ac_proto::MotionItem {
                        command: 0x0057,
                        packed_sequence: action_stamp,
                        speed: 1.0,
                    }],
                },
                sticky_object: None,
            },
        },
    }))
}

#[test]
fn the_view_carries_the_accepted_motion_until_the_physics_takes_it() {
    let mut world = world_with_door();
    assert_eq!(world.snapshot().objects[0].motion, None);
    world.apply(&walking(DOOR, 1, 3));
    let view = world.snapshot().objects[0].motion.clone().unwrap();
    // Walk forward (0x45000005) at 1.5, the stance from the header, on the
    // ground, and the action with the server's stamp.
    assert_eq!(view.state.forward_command, 0x4500_0005);
    assert_eq!(view.state.forward_speed, 1.5);
    assert_eq!(view.state.current_style, 0x8000_003D);
    assert!(view.grounded);
    let actions = view.state.actions.clone().unwrap();
    assert_eq!((actions.len(), actions[0].stamp), (1, 3));
    world.drain_changes().for_each(drop);

    // The physics publishes its interpreter: one motion change, the axes
    // are the interpreter's, the actions stay the server's.
    let mut live = view.clone();
    live.state.forward_command = 0x4400_0007;
    live.state.actions = None;
    live.grounded = false;
    world.set_live_motion(DOOR, live.clone());
    world.set_live_motion(DOOR, live.clone());
    assert_eq!(
        physics_changes(&mut world),
        [WorldChange::MotionChanged(
            world.object(DOOR).unwrap().key()
        )]
    );
    let shown = world.snapshot().objects[0].motion.clone().unwrap();
    assert_eq!(shown.state.forward_command, 0x4400_0007);
    assert!(!shown.grounded);
    assert_eq!(shown.state.actions, view.state.actions);

    // The physics lets go: the server's state again.
    world.clear_live_position(DOOR);
    assert_eq!(world.snapshot().objects[0].motion, Some(view));
}

/// A window draws a stepped body by the physics' own smoothed place, at its
/// frame's time: from the step before the last toward the last by the share
/// of a step elapsed since the last one ended, held at the two ends outside
/// it. Golden values from OpenAC `PlayerMovementController
/// .ComputeRenderPosition`: `Vector3.Lerp(prev, curr, clamp(pending /
/// lastQuantum, 0, 1))`, here with a step of 1/30 s ending at 5 s and frames
/// 1/120 s and 1/60 s after it (a quarter and a half of a step). A jump
/// leaves both places the same: nothing blends across it.
#[test]
fn a_step_blend_draws_two_frames_between_two_steps_by_their_own_time() {
    use std::time::Duration;
    let v = |x: f32, y: f32, z: f32| Vector3 { x, y, z };
    let blend = StepBlend {
        previous: v(10.0, 20.0, 0.0),
        current: v(10.4, 20.3, 0.1),
        step_at: Duration::from_secs(5),
        step_seconds: 1.0 / 30.0,
    };
    let near = |a: Vector3, b: Vector3| {
        (a.x - b.x).abs() < 1e-5 && (a.y - b.y).abs() < 1e-5 && (a.z - b.z).abs() < 1e-5
    };
    let at = |nanos: u64| blend.origin_at(Duration::from_secs(5) + Duration::from_nanos(nanos));
    let quarter = at(8_333_333);
    assert!(near(quarter, v(10.1, 20.075, 0.025)), "{quarter:?}");
    let half = at(16_666_667);
    assert!(near(half, v(10.2, 20.15, 0.05)), "{half:?}");
    assert_eq!(
        blend.origin_at(Duration::from_millis(4_900)),
        blend.previous,
        "before the step ended"
    );
    assert_eq!(at(100_000_000), blend.current, "a whole step later");

    let jumped = StepBlend {
        previous: v(50.0, 60.0, 1.0),
        current: v(50.0, 60.0, 1.0),
        ..blend
    };
    for nanos in [0, 8_333_333, 16_666_667, 40_000_000] {
        let t = Duration::from_secs(5) + Duration::from_nanos(nanos);
        assert_eq!(jumped.origin_at(t), v(50.0, 60.0, 1.0));
    }
}

/// A body the session's physics moves in fixed steps carries, in a window's
/// object view and snapshot, what the window blends to draw it at its own
/// frame's time; the view's place, like the world's (gameplay, plugins),
/// is the body on its step. A new step is a move for a window to follow;
/// a pass with no new step, rounding in the step's end, or a body standing
/// still whose steps go on ending is not. Once the physics lets go the blend
/// goes with it.
#[test]
fn the_change_stream_carries_the_step_blend_while_the_world_keeps_the_step() {
    use std::time::Duration;
    let mut world = world_with_door();
    let key = world.object(DOOR).unwrap().key();
    let moved_once = [WorldChange::Moved {
        key,
        teleported: false,
        forced: false,
    }];
    let at = |x: f32| position(0xA9B4_0031, x).origin;
    let blend = |from: f32, to: f32, ms: u64| StepBlend {
        previous: at(from),
        current: at(to),
        step_at: Duration::from_millis(ms),
        step_seconds: 1.0 / 30.0,
    };
    // No live body: nothing to blend.
    world.set_step_blend(DOOR, blend(5.5, 6.0, 100));
    assert!(physics_changes(&mut world).is_empty());
    assert_eq!(world.snapshot().objects[0].step_blend, None);

    world.set_live_position(DOOR, position(0xA9B4_0031, 6.0));
    world.set_step_blend(DOOR, blend(5.5, 6.0, 100));
    physics_changes(&mut world);
    let view = world.snapshot().objects[0].clone();
    assert_eq!(view.position.map(|p| p.origin.x), Some(6.0), "the step");
    assert_eq!(view.step_blend, Some(blend(5.5, 6.0, 100)));

    // The next step: its blend alone is a move, carried in the delta.
    world.set_live_position(DOOR, position(0xA9B4_0031, 6.5));
    physics_changes(&mut world);
    world.set_step_blend(DOOR, blend(6.0, 6.5, 133));
    let changes = physics_changes(&mut world);
    assert_eq!(changes, moved_once);
    let deltas = world.deltas(&changes);
    let [ObjectDelta::Upsert(view)] = deltas.as_slice() else {
        panic!("one upsert: {deltas:?}");
    };
    assert_eq!(view.position.map(|p| p.origin.x), Some(6.5));
    assert_eq!(view.step_blend, Some(blend(6.0, 6.5, 133)));

    // A pass with no new step, its end off by rounding: nothing to follow.
    let mut rounded = blend(6.0, 6.5, 133);
    rounded.step_at += Duration::from_nanos(3);
    world.set_step_blend(DOOR, rounded);
    assert!(physics_changes(&mut world).is_empty(), "unchanged");
    assert_eq!(
        world.snapshot().objects[0].step_blend,
        Some(blend(6.0, 6.5, 133)),
        "the window's copy is the world's"
    );

    // Standing still, the steps go on ending without changing the drawing.
    world.set_step_blend(DOOR, blend(6.5, 6.5, 166));
    assert_eq!(physics_changes(&mut world), moved_once, "stopped");
    world.set_step_blend(DOOR, blend(6.5, 6.5, 200));
    assert!(physics_changes(&mut world).is_empty(), "standing still");

    world.clear_live_position(DOOR);
    physics_changes(&mut world);
    let view = world.snapshot().objects[0].clone();
    assert_eq!(view.step_blend, None);
    assert_eq!(view.position.map(|p| p.origin.x), Some(5.0));
}

/// An object whose entry into the world found no clear spot stays in the
/// table (its records and updates) but a window does not draw it (OpenAC:
/// no cell, the projection is not spatially visible), for the rest of its
/// incarnation; a new incarnation is in the world again.
#[test]
fn an_object_refused_entry_is_known_but_not_drawn_until_it_is_created_again() {
    let mut world = world_with_door();
    world.set_live_position(DOOR, position(0xA9B4_0031, 6.0));
    world.drain_changes().for_each(drop);
    world.refuse_entry(DOOR);
    let key = world.object(DOOR).unwrap().key();
    assert_eq!(
        physics_changes(&mut world),
        [WorldChange::Moved {
            key,
            teleported: false,
            forced: false,
        }]
    );
    let object = world.object(DOOR).unwrap();
    assert!(object.entry_refused());
    assert_eq!(object.position().map(|p| p.origin.x), Some(5.0));
    assert_eq!(world.snapshot().objects[0].position, None);
    world.refuse_entry(DOOR);
    assert!(physics_changes(&mut world).is_empty(), "once");

    // Its updates are still taken.
    world.apply(&moved(DOOR, 1, 11, 7.0));
    assert_eq!(
        world
            .object(DOOR)
            .unwrap()
            .accepted_position()
            .map(|p| p.origin.x),
        Some(7.0)
    );
    assert_eq!(world.snapshot().objects[0].position, None);

    world.apply(&create(
        DOOR,
        stamps(2, 1),
        Some(position(0xA9B4_0031, 8.0)),
    ));
    assert!(!world.object(DOOR).unwrap().entry_refused());
    assert_eq!(
        world.snapshot().objects[0].position.map(|p| p.origin.x),
        Some(8.0)
    );
}

/// An object only the server moves takes up every accepted motion record,
/// the same one again too, in order (OpenAC applies each `UpdateMotion`);
/// one the session's physics moves records its own (the movement owner),
/// and a stale record is not taken up.
#[test]
fn every_accepted_motion_of_a_server_only_object_is_recorded_in_order() {
    let mut world = world_with_door();
    world.drain_applied_motions().for_each(drop);
    world.apply(&walking(DOOR, 1, 3));
    world.apply(&walking(DOOR, 2, 3));
    world.apply(&walking(DOOR, 2, 3));
    let taken: Vec<_> = world.drain_applied_motions().collect();
    let serials: Vec<u32> = taken.iter().map(|m| m.motion.serial).collect();
    assert_eq!(serials, [1, 2], "the stale copy is not taken up");
    assert!(
        taken
            .iter()
            .all(|m| m.guid == DOOR && m.motion.body.is_none())
    );
    assert_eq!(taken[0].motion.state, taken[1].motion.state);

    let mut live = taken[1].motion.clone();
    live.body = Some(crate::world::BodyFacts::default());
    world.set_live_motion(DOOR, live);
    world.apply(&walking(DOOR, 3, 4));
    assert_eq!(world.drain_applied_motions().count(), 0);
}

#[test]
fn the_game_clock_survives_a_log_off_but_not_its_connection() {
    // OpenAC `RuntimeWorldEnvironmentState`: the world time is a lifetime
    // owner, never reset when the character leaves; the sky of a character
    // entering again follows the last sync at once.
    use std::time::Duration;
    let mut world = World::new();
    world.set_player(PLAYER);
    world.sync_time(1_000.0, Duration::from_secs(10));
    world.reset_session();
    assert_eq!(world.player_guid(), None, "the character is forgotten");
    assert_eq!(
        world.clock().now_ticks(Duration::from_secs(25)),
        Some(1_015.0),
        "the clock is not"
    );
    world.forget_clock();
    assert_eq!(world.clock().now_ticks(Duration::from_secs(25)), None);
}

#[test]
fn a_fog_override_is_world_state_and_a_sound_cue_is_not() {
    // OpenAC `RuntimeWorldEnvironmentStateTests`: AdminEnvirons 0..=6 set
    // the weather's override and stay; other values are sound cues that
    // leave it alone; the environment outlives a log off.
    let environ = |value: u32| {
        let mut body = 0xEA60u32.to_le_bytes().to_vec();
        body.extend_from_slice(&value.to_le_bytes());
        body
    };
    let mut world = World::new();
    assert_eq!(world.environ_override(), None);
    world.apply(&Inbound::parse(&environ(0x02)).unwrap());
    assert_eq!(world.environ_override(), Some(0x02));
    world.apply(&Inbound::parse(&environ(0x76)).unwrap());
    assert_eq!(world.environ_override(), Some(0x02), "thunder is a cue");
    world.reset_session();
    assert_eq!(world.environ_override(), Some(0x02));
    world.apply(&Inbound::parse(&environ(0x00)).unwrap());
    assert_eq!(world.environ_override(), Some(0x00), "0 clears the fog");
}

/// OpenAC `SetupPartTransformsTests.ObjectPlacementId_FollowsWhatTheServerSent`:
/// no placement and no motion data rests in the default placement, a named
/// placement in that one, and motion data in the resting one.
#[test]
fn an_object_rests_in_the_placement_its_description_names() {
    let mut desc = physics(stamps(1, 1), None);
    assert_eq!(rest_placement(&desc), 0);

    desc.animation_frame = Some(0x67);
    assert_eq!(rest_placement(&desc), 0x67);

    desc.animation_frame = None;
    desc.movement = Some(ac_proto::PhysicsMovement::Present {
        raw: vec![0; 4],
        data: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: 0x003D,
            movement: ac_proto::Movement::Interpreted {
                state: ac_proto::InterpretedMotionState::default(),
                sticky_object: None,
            },
        },
        is_autonomous: false,
    });
    assert_eq!(rest_placement(&desc), 0x65);
}
