//! Ported from OpenAC `tests/AcDream.Core.Tests/Items/ClientObjectTableTests.cs`,
//! `ClientObjectTableUpdateTests.cs` and `ClientObjectTableBurdenTests.cs`.
//!
//! OpenAC's re-entrancy tests (an observer mutating the table from inside
//! an event) become "apply the follow-up after the call": events are
//! returned once the table is consistent, so the follow-up sees the
//! committed state by construction.

use super::*;
use crate::items::ItemUseable;

const PLAYER: u32 = 0x5000_0001;

fn make_item(id: u32, name: &str) -> ClientObject {
    let mut item = ClientObject::new(id);
    item.weenie_class_id = 1;
    item.name = name.into();
    item.item_type = ItemType::MISC;
    item.stack_size = 1;
    item.burden = 10;
    item.value = 5;
    item
}

fn full_weenie(guid: u32) -> WeenieData {
    WeenieData {
        guid,
        name: Some("Sword".into()),
        item_type: Some(ItemType::MELEE_WEAPON),
        weenie_class_id: 0xABCD,
        icon_id: 0x0600_1234,
        value: Some(100),
        stack_size: Some(1),
        stack_size_max: Some(1),
        burden: Some(10),
        ..WeenieData::default()
    }
}

fn in_container(guid: u32, container: u32) -> WeenieData {
    WeenieData {
        container_id: Some(container),
        ..full_weenie(guid)
    }
}

fn entry(guid: u32, container_type: u32) -> ContainerContentEntry {
    ContainerContentEntry {
        guid,
        container_type,
    }
}

fn wielded(wielder_id: u32, equip_location: EquipMask) -> ClientObjectPlacement {
    ClientObjectPlacement {
        container_id: 0,
        container_slot: -1,
        wielder_id,
        equip_location,
    }
}

struct Fixture {
    t: ClientObjectTable,
    ev: Vec<TableEvent>,
}

impl Fixture {
    fn new() -> Self {
        Self {
            t: ClientObjectTable::new(),
            ev: Vec::new(),
        }
    }

    fn add(&mut self, item: ClientObject) -> ObjectIdentity {
        self.t.add_or_update(item, &mut self.ev)
    }

    fn ingest(&mut self, data: WeenieData) -> ObjectIdentity {
        self.t.ingest(&data, &mut self.ev)
    }

    fn mv(&mut self, id: u32, container: u32, slot: i32) -> bool {
        self.t
            .move_item(id, container, slot, EquipMask::NONE, None, &mut self.ev)
    }

    fn server_move(&mut self, id: u32, container: u32, slot: i32) -> bool {
        self.t.apply_server_move(
            id,
            ClientObjectPlacement::in_container(container, slot),
            None,
            &mut self.ev,
        )
    }

    fn confirmed_move(&mut self, id: u32, container: u32, slot: i32, hint: u32) -> bool {
        self.t.apply_confirmed_server_move(
            id,
            ClientObjectPlacement::in_container(container, slot),
            Some(hint),
            &mut self.ev,
        )
    }

    fn contents(&self, container: u32) -> Vec<u32> {
        self.t.get_contents(container).to_vec()
    }

    fn get(&self, id: u32) -> &ClientObject {
        self.t.get(id).expect("object present")
    }

    fn take(&mut self) -> Vec<TableEvent> {
        std::mem::take(&mut self.ev)
    }

    fn equipped_ids(&self, wielder: u32) -> Vec<u32> {
        self.t
            .get_equipped_by(wielder)
            .iter()
            .map(|item| item.object_id())
            .collect()
    }
}

fn moves(events: &[TableEvent]) -> Vec<ObjectMove> {
    events
        .iter()
        .filter_map(|event| match event {
            TableEvent::Moved(m) => Some(*m),
            _ => None,
        })
        .collect()
}

fn replaced(events: &[TableEvent]) -> Vec<u32> {
    events
        .iter()
        .filter_map(|event| match event {
            TableEvent::ContainerContentsReplaced(id) => Some(*id),
            _ => None,
        })
        .collect()
}

fn count_updated(events: &[TableEvent]) -> usize {
    events
        .iter()
        .filter(|event| matches!(event, TableEvent::Updated { .. }))
        .count()
}

#[test]
fn add_or_update_reports_added() {
    let mut f = Fixture::new();
    let identity = f.add(make_item(100, "Widget"));
    assert_eq!(
        f.take(),
        vec![TableEvent::Added {
            object_id: 100,
            identity
        }]
    );
    assert_eq!(f.t.object_count(), 1);
    assert_eq!(f.get(100).name, "Widget");
}

#[test]
fn add_or_update_existing_item_reports_updated() {
    let mut f = Fixture::new();
    f.add(make_item(100, "Widget"));
    f.take();
    f.add(make_item(100, "Widget"));
    assert_eq!(count_updated(&f.take()), 1);
}

#[test]
fn is_owned_by_object_matches_retail_direct_pack_and_location_rules() {
    let pack = 0x4000_0001;
    let mut f = Fixture::new();
    f.add(make_item(PLAYER, "Player"));
    let mut pack_item = ClientObject::new(pack);
    pack_item.name = "Pack".into();
    pack_item.item_type = ItemType::CONTAINER;
    pack_item.container_id = PLAYER;
    f.add(pack_item);
    f.t.replace_contents(PLAYER, &[entry(pack, 1)], &mut f.ev);

    let mut loose = make_item(100, "Loose");
    loose.container_id = PLAYER;
    f.add(loose);
    let mut packed = make_item(101, "Packed");
    packed.container_id = pack;
    f.add(packed);
    let mut equipped = make_item(102, "Equipped");
    equipped.wielder_id = PLAYER;
    equipped.currently_equipped_location = EquipMask::MELEE_WEAPON;
    f.add(equipped);
    let mut foreign = make_item(103, "Foreign");
    foreign.container_id = 0x6000_0001;
    f.add(foreign);

    assert!(f.t.is_owned_by_object(PLAYER, PLAYER));
    assert!(f.t.is_owned_by_object(100, PLAYER));
    assert!(f.t.is_owned_by_object(101, PLAYER));
    assert!(f.t.is_owned_by_object(102, PLAYER));
    assert!(!f.t.is_owned_by_object(103, PLAYER));
    assert!(!f.t.is_owned_by_object(101, 0));
}

#[test]
fn stop_viewing_contents_removes_only_temporary_projection() {
    let mut f = Fixture::new();
    let mut item = make_item(100, "Widget");
    item.container_id = 77;
    f.add(item);
    f.t.replace_contents_ids(77, &[100], &mut f.ev);
    f.take();

    assert!(f.t.stop_viewing_contents(77, &mut f.ev));

    assert!(f.contents(77).is_empty());
    assert_eq!(f.get(100).container_id, 77);
    assert_eq!(replaced(&f.take()), vec![77]);
    assert!(!f.t.stop_viewing_contents(77, &mut f.ev));
}

#[test]
fn stop_viewing_contents_tree_removes_nested_snapshots_but_preserves_objects() {
    let mut f = Fixture::new();
    f.t.replace_contents(77, &[entry(88, 1)], &mut f.ev);
    f.t.replace_contents(88, &[entry(100, 0)], &mut f.ev);

    assert_eq!(f.t.stop_viewing_contents_tree(77, &mut f.ev), 2);

    assert!(f.contents(77).is_empty());
    assert!(f.contents(88).is_empty());
    assert!(f.t.get(88).is_some());
    assert!(f.t.get(100).is_some());
}

#[test]
fn move_item_updates_container_and_reports_move() {
    let mut f = Fixture::new();
    f.add(make_item(100, "Widget"));
    f.take();

    assert!(f.mv(100, 42, 3));

    let seen = moves(&f.take());
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0].previous.container_id, 0);
    assert_eq!(seen[0].current.container_id, 42);
    assert_eq!(f.get(100).container_id, 42);
    assert_eq!(f.get(100).container_slot, 3);
}

#[test]
fn move_item_nonexistent_returns_false() {
    let mut f = Fixture::new();
    assert!(!f.mv(999, 42, -1));
}

#[test]
fn apply_server_move_publishes_complete_old_and_new_placements() {
    let (pack, item_id) = (0x4000_0005, 101);
    let mut f = Fixture::new();
    let mut item = ClientObject::new(item_id);
    item.wielder_id = PLAYER;
    item.currently_equipped_location = EquipMask::MISSILE_WEAPON;
    f.add(item);
    f.take();

    assert!(f.server_move(item_id, pack, 3));

    let seen = moves(&f.take());
    assert_eq!(
        seen[0].previous,
        ClientObjectPlacement {
            container_id: 0,
            container_slot: -1,
            wielder_id: PLAYER,
            equip_location: EquipMask::MISSILE_WEAPON
        }
    );
    assert_eq!(
        seen[0].current,
        ClientObjectPlacement::in_container(pack, 3)
    );
}

#[test]
fn confirmed_unknown_move_publishes_guid_and_raw_placement() {
    let mut f = Fixture::new();
    assert!(!f.t.apply_confirmed_server_move(
        0xDEAD,
        ClientObjectPlacement::in_container(0x4000_0001, 7),
        None,
        &mut f.ev
    ));
    let seen = moves(&f.take());
    assert_eq!(seen[0].item_id, 0xDEAD);
    assert_eq!(seen[0].identity, None);
    assert_eq!(seen[0].previous, ClientObjectPlacement::default());
    assert_eq!(
        seen[0].current,
        ClientObjectPlacement::in_container(0x4000_0001, 7)
    );
}

#[test]
fn confirmed_unknown_wield_publishes_guid_placement_and_completion() {
    let item_id = 0xBEEF;
    let mut f = Fixture::new();
    assert!(!f.t.apply_confirmed_server_wield(
        item_id,
        PLAYER,
        EquipMask::MISSILE_WEAPON,
        &mut f.ev
    ));
    let events = f.take();
    let seen = moves(&events);
    assert_eq!(seen[0].item_id, item_id);
    assert_eq!(seen[0].identity, None);
    assert_eq!(
        seen[0].current,
        ClientObjectPlacement {
            container_id: 0,
            container_slot: 0,
            wielder_id: PLAYER,
            equip_location: EquipMask::MISSILE_WEAPON
        }
    );
    assert!(events.contains(&TableEvent::WieldConfirmed(item_id)));
}

#[test]
fn remove_reports_and_removes() {
    let mut f = Fixture::new();
    f.add(make_item(100, "Widget"));
    f.take();
    assert!(f.t.remove(100, &mut f.ev));
    let events = f.take();
    assert!(matches!(
        &events[0],
        TableEvent::Removed { object, reason: RemovalReason::Ordinary, generation: 0 }
            if object.object_id() == 100
    ));
    assert!(f.t.get(100).is_none());
}

#[test]
fn update_properties_merges_incoming_bundle() {
    let mut f = Fixture::new();
    let mut item = make_item(100, "Widget");
    item.properties.ints.insert(1, 10);
    f.add(item);
    let mut patch = PropertyBundle::default();
    patch.ints.insert(2, 20);
    patch.ints.insert(1, 15);
    patch.strings.insert(100, "desc".into());

    assert!(f.t.update_properties(100, &patch, &mut f.ev));

    let props = &f.get(100).properties;
    assert_eq!(props.ints[&1], 15);
    assert_eq!(props.ints[&2], 20);
    assert_eq!(props.strings[&100], "desc");
}

#[test]
fn update_properties_projects_assessed_shared_cooldown_metadata() {
    let mut f = Fixture::new();
    f.add(make_item(100, "Widget"));
    let mut patch = PropertyBundle::default();
    patch.ints.insert(PropertyInt::SHARED_COOLDOWN.0, 42);
    patch
        .floats
        .insert(PropertyFloat::COOLDOWN_DURATION.0, 30.0);

    f.t.update_properties(100, &patch, &mut f.ev);

    assert_eq!(f.get(100).cooldown_id, Some(42));
    assert_eq!(f.get(100).cooldown_duration, Some(30.0));
}

#[test]
fn clear_removes_all_items() {
    let mut f = Fixture::new();
    for id in 1..=3 {
        f.add(make_item(id, "Widget"));
    }
    f.t.clear(&mut f.ev);
    assert_eq!(f.t.object_count(), 0);
    assert_eq!(f.take().last(), Some(&TableEvent::Cleared));
}

#[test]
fn update_int_property_ui_effects_sets_effects_and_reports() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x5000_00AB));
    f.take();
    assert!(f.t.update_int_property(0x5000_00AB, PropertyInt::UI_EFFECTS.0, 0x9, &mut f.ev));
    assert_eq!(f.get(0x5000_00AB).effects, 0x9);
    assert_eq!(
        f.get(0x5000_00AB).properties.ints[&PropertyInt::UI_EFFECTS.0],
        0x9
    );
    assert_eq!(count_updated(&f.take()), 1);
}

#[test]
fn update_int_property_unknown_item_returns_false() {
    let mut f = Fixture::new();
    assert!(!f.t.update_int_property(0xDEAD_BEEF, 18, 1, &mut f.ev));
}

#[test]
fn update_int_property_ui_effects_cleared_to_zero_clears_effects() {
    let mut f = Fixture::new();
    let mut item = ClientObject::new(0x5000_00AC);
    item.effects = 1;
    f.add(item);
    f.t.update_int_property(0x5000_00AC, PropertyInt::UI_EFFECTS.0, 1, &mut f.ev);
    assert_eq!(f.get(0x5000_00AC).effects, 1);
    f.t.update_int_property(0x5000_00AC, PropertyInt::UI_EFFECTS.0, 0, &mut f.ev);
    assert_eq!(f.get(0x5000_00AC).effects, 0);
}

#[test]
fn update_int_property_structure_mirrors_onto_typed_fields() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x5000_00AE));
    assert!(f.t.update_int_property(0x5000_00AE, 92, 4, &mut f.ev));
    assert!(f.t.update_int_property(0x5000_00AE, 91, 10, &mut f.ev));
    assert_eq!(f.get(0x5000_00AE).structure, 4);
    assert_eq!(f.get(0x5000_00AE).max_structure, 10);
}

// OpenAC v0.1.25 (#195) `ClientObjectTableTests`: an augmented
// pack capacity reaches the typed fields on every path.
#[test]
fn live_capacity_updates_reach_the_typed_capacities() {
    // LiveCapacityUpdates_publishTypedCapacitiesBeforeNotification.
    let mut f = Fixture::new();
    let mut player = ClientObject::new(0x5000_00AE);
    player.containers_capacity = 7;
    f.add(player);
    f.take();
    assert!(f.t.update_int_property(0x5000_00AE, 7, 8, &mut f.ev));
    assert_eq!(
        (
            f.get(0x5000_00AE).items_capacity,
            f.get(0x5000_00AE).containers_capacity
        ),
        (0, 8)
    );
    assert!(f.t.update_int_property(0x5000_00AE, 6, 102, &mut f.ev));
    assert_eq!(
        (
            f.get(0x5000_00AE).items_capacity,
            f.get(0x5000_00AE).containers_capacity
        ),
        (102, 8)
    );
    assert_eq!(count_updated(&f.take()), 2);
    assert_eq!(f.get(0x5000_00AE).properties.ints[&7], 8);
    assert_eq!(f.get(0x5000_00AE).properties.ints[&6], 102);
}

#[test]
fn capacity_property_bundles_update_the_typed_fields_and_keep_omitted_values() {
    // CapacityPropertyBundles_updateTypedFieldsAndPreserveOmittedValues:
    // the login, update and appraisal paths.
    for path in ["login", "update", "appraisal"] {
        let mut f = Fixture::new();
        let mut player = ClientObject::new(0x5000_00AE);
        player.items_capacity = 102;
        player.containers_capacity = 7;
        f.add(player);
        let mut incoming = PropertyBundle::default();
        incoming.ints.insert(7, 8);
        incoming.ints.insert(6, 120);
        let apply = |f: &mut Fixture, properties: PropertyBundle| match path {
            "login" => f.t.upsert_properties(0x5000_00AE, &properties, &mut f.ev),
            "update" => assert!(f.t.update_properties(0x5000_00AE, &properties, &mut f.ev)),
            _ => assert!(f.t.update_appraisal(
                0x5000_00AE,
                &AppraisalUpdate {
                    properties,
                    spell_ids: Vec::new(),
                    received_at_seconds: -1.0,
                    weapon_profile: None,
                    armor_profile: None,
                },
                &mut f.ev
            )),
        };
        apply(&mut f, incoming);
        apply(&mut f, PropertyBundle::default());
        assert_eq!(f.get(0x5000_00AE).containers_capacity, 8, "{path}");
        assert_eq!(f.get(0x5000_00AE).items_capacity, 120, "{path}");
    }
}

#[test]
fn update_int_property_current_wielded_location_updates_typed_equip_location() {
    let mut f = Fixture::new();
    let mut item = ClientObject::new(0x5000_00AD);
    item.currently_equipped_location = EquipMask::CHEST_ARMOR;
    f.add(item);
    assert!(f.t.update_int_property(
        0x5000_00AD,
        PropertyInt::CURRENT_WIELDED_LOCATION.0,
        0,
        &mut f.ev
    ));
    assert_eq!(
        f.get(0x5000_00AD).currently_equipped_location,
        EquipMask::NONE
    );
    assert_eq!(
        f.get(0x5000_00AD).properties.ints[&PropertyInt::CURRENT_WIELDED_LOCATION.0],
        0
    );
}

#[test]
fn update_int_property_player_killer_status_sets_public_weenie_bitfield() {
    let mut f = Fixture::new();
    let mut item = ClientObject::new(0x5000_00AF);
    item.public_weenie_bitfield = Some(PublicWeenieFlags::PLAYER);
    f.add(item);
    f.take();
    assert!(f.t.update_int_property(
        0x5000_00AF,
        PropertyInt::PLAYER_KILLER_STATUS.0,
        pk_status::PK_LITE,
        &mut f.ev
    ));
    assert_eq!(
        f.get(0x5000_00AF).public_weenie_bitfield,
        Some(PublicWeenieFlags(0x200_0008))
    );
    assert_eq!(
        f.get(0x5000_00AF).properties.ints[&PropertyInt::PLAYER_KILLER_STATUS.0],
        pk_status::PK_LITE
    );
    assert_eq!(count_updated(&f.take()), 1);
}

#[test]
fn update_int_property_player_killer_status_missing_bitfield_is_zero() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x5000_00B4));
    f.t.update_int_property(
        0x5000_00B4,
        PropertyInt::PLAYER_KILLER_STATUS.0,
        pk_status::PK,
        &mut f.ev,
    );
    assert_eq!(
        f.get(0x5000_00B4).public_weenie_bitfield,
        Some(PublicWeenieFlags(0x20))
    );
}

#[test]
fn update_bool_property_ui_hidden_sets_and_clears_the_public_bit() {
    // The UI-hidden bool (24) moves the description's UI-hidden bit (0x80)
    // and leaves the other bits alone: a hook the server makes visible can
    // be picked again.
    use crate::properties::PropertyBool;
    let mut f = Fixture::new();
    let mut hook = ClientObject::new(0x5000_00B5);
    hook.public_weenie_bitfield = Some(PublicWeenieFlags::STUCK | PublicWeenieFlags::UI_HIDDEN);
    f.add(hook);
    f.take();
    assert!(f.t.update_bool_property(0x5000_00B5, PropertyBool::UI_HIDDEN.0, false, &mut f.ev));
    assert_eq!(
        f.get(0x5000_00B5).public_weenie_bitfield,
        Some(PublicWeenieFlags::STUCK)
    );
    assert_eq!(count_updated(&f.take()), 1);
    f.t.update_bool_property(0x5000_00B5, PropertyBool::UI_HIDDEN.0, true, &mut f.ev);
    assert_eq!(
        f.get(0x5000_00B5).public_weenie_bitfield,
        Some(PublicWeenieFlags::STUCK | PublicWeenieFlags::UI_HIDDEN)
    );
    // An unrelated bool leaves the bits.
    f.t.update_bool_property(0x5000_00B5, PropertyBool::OPEN.0, true, &mut f.ev);
    assert_eq!(
        f.get(0x5000_00B5).public_weenie_bitfield,
        Some(PublicWeenieFlags::STUCK | PublicWeenieFlags::UI_HIDDEN)
    );
}

#[test]
fn a_description_bool_update_moves_its_description_bit() {
    // DescriptionBoolUpdate_MovesItsDescriptionBit: each of these bools is
    // also a bit of the public description, and the update moves that bit
    // with it, leaving every other bit alone. Locked (3) is the inverse of
    // openable (0x1): unlocking a chest with a key makes it openable.
    use crate::properties::PropertyBool;
    let cases = [
        (PropertyBool::STUCK, PublicWeenieFlags::STUCK, false),
        (PropertyBool::LOCKED, PublicWeenieFlags::OPENABLE, true),
        (
            PropertyBool::INSCRIBABLE,
            PublicWeenieFlags::INSCRIBABLE,
            false,
        ),
        (PropertyBool::UI_HIDDEN, PublicWeenieFlags::UI_HIDDEN, false),
        (
            PropertyBool::IGNORE_HOUSE_BARRIERS,
            PublicWeenieFlags::IMMUNE_CELL_RESTRICTIONS,
            false,
        ),
        (
            PropertyBool::HIDDEN_ADMIN,
            PublicWeenieFlags::HIDDEN_ADMIN,
            false,
        ),
    ];
    let guid = 0x7E16_6010;
    let other = PublicWeenieFlags::ATTACKABLE | PublicWeenieFlags::BINDSTONE;
    for (property, bit, inverted) in cases {
        let mut f = Fixture::new();
        let mut object = ClientObject::new(guid);
        object.public_weenie_bitfield = Some(other);
        f.add(object);
        assert!(f.t.update_bool_property(guid, property.0, !inverted, &mut f.ev));
        assert_eq!(
            f.get(guid).public_weenie_bitfield,
            Some(other | bit),
            "{property:?} set"
        );
        assert!(f.t.update_bool_property(guid, property.0, inverted, &mut f.ev));
        assert_eq!(
            f.get(guid).public_weenie_bitfield,
            Some(other),
            "{property:?} cleared"
        );
    }
}

#[test]
fn ingest_and_live_properties_preserve_hook_identity() {
    let guid = 0x5000_00AE;
    let mut f = Fixture::new();
    f.ingest(WeenieData {
        hook_item_types: Some(ItemType::CONTAINER.bits()),
        hook_type: Some(4),
        ..full_weenie(guid)
    });
    assert!(f.get(guid).is_hook());
    assert_eq!(f.get(guid).hook_item_types, ItemType::CONTAINER.bits());
    assert_eq!(f.get(guid).hook_type, 4);

    assert!(f.t.update_int_property(guid, PropertyInt::HOOK_TYPE.0, 0, &mut f.ev));
    assert!(!f.get(guid).is_hook());
    assert!(f.t.update_int_property(guid, PropertyInt::HOOK_TYPE.0, 2, &mut f.ev));
    assert!(f.t.update_int_property(
        guid,
        PropertyInt::HOOK_ITEM_TYPE.0,
        ItemType::MISC.bits() as i32,
        &mut f.ev
    ));
    assert!(f.get(guid).is_hook());
    assert_eq!(f.get(guid).hook_type, 2);
    assert_eq!(f.get(guid).hook_item_types, ItemType::MISC.bits());
}

#[test]
fn ingest_pet_owner_from_second_header_is_preserved() {
    let mut f = Fixture::new();
    f.ingest(WeenieData {
        guid: 0x5000_0010,
        name: Some("Combat Pet".into()),
        item_type: Some(ItemType::CREATURE),
        weenie_class_id: 2,
        pet_owner_id: Some(PLAYER),
        ..WeenieData::default()
    });
    assert_eq!(f.get(0x5000_0010).pet_owner_id, PLAYER);
}

#[test]
fn ingest_new_item_with_no_prior_stub_creates_and_reports_added() {
    let mut f = Fixture::new();
    let identity = f.ingest(full_weenie(0x5000_00B0));
    assert!(f.take().contains(&TableEvent::Added {
        object_id: 0x5000_00B0,
        identity
    }));
    assert_eq!(f.get(0x5000_00B0).icon_id, 0x0600_1234);
    assert_eq!(f.get(0x5000_00B0).weenie_class_id, 0xABCD);
}

#[test]
fn ingest_existing_patches_in_place_and_preserves_property_bundle() {
    let mut f = Fixture::new();
    let first = f.ingest(full_weenie(0x5000_00B1));
    f.t.get_mut(0x5000_00B1)
        .expect("present")
        .properties
        .ints
        .insert(999, 7);
    f.take();
    let second = f.ingest(WeenieData {
        name: Some("Renamed".into()),
        ..full_weenie(0x5000_00B1)
    });
    assert_eq!(first, second);
    assert_eq!(count_updated(&f.take()), 1);
    assert_eq!(f.get(0x5000_00B1).name, "Renamed");
    assert_eq!(f.get(0x5000_00B1).properties.ints[&999], 7);
}

#[test]
fn ingest_absent_optional_field_does_not_clobber() {
    let mut f = Fixture::new();
    f.ingest(full_weenie(0x5000_00B2));
    f.ingest(WeenieData {
        value: None,
        ..full_weenie(0x5000_00B2)
    });
    assert_eq!(f.get(0x5000_00B2).value, 100);
}

#[test]
fn ingest_effects_assigned_unconditionally_clears_to_zero() {
    let mut f = Fixture::new();
    f.ingest(WeenieData {
        effects: 1,
        ..full_weenie(0x5000_00B3)
    });
    assert_eq!(f.get(0x5000_00B3).effects, 1);
    f.ingest(full_weenie(0x5000_00B3));
    assert_eq!(f.get(0x5000_00B3).effects, 0);
}

#[test]
fn ingest_useability_and_target_type_assign_typed_fields() {
    let mut f = Fixture::new();
    f.ingest(WeenieData {
        useability: Some(ItemUseable(0x000A_0008)),
        target_type: Some(ItemType::CREATURE),
        ..full_weenie(0x5000_00B7)
    });
    assert_eq!(
        f.get(0x5000_00B7).useability,
        Some(ItemUseable(0x000A_0008))
    );
    assert_eq!(f.get(0x5000_00B7).target_type, Some(ItemType::CREATURE));
}

#[test]
fn ingest_plural_name_preserves_wire_value() {
    let mut f = Fixture::new();
    f.ingest(WeenieData {
        name: Some("Pyreal Scarab".into()),
        stack_size: Some(2),
        plural_name: Some("Pyreal Scarabs".into()),
        ..full_weenie(0x5000_00B9)
    });
    assert_eq!(f.get(0x5000_00B9).plural_name, "Pyreal Scarabs");
    assert_eq!(f.get(0x5000_00B9).appropriate_name(), "Pyreal Scarabs");
}

#[test]
fn ingest_material_type_preserves_wire_value_without_clobbering_when_absent() {
    let mut f = Fixture::new();
    f.ingest(WeenieData {
        material_type: Some(77),
        ..full_weenie(0x5000_00BC)
    });
    f.ingest(full_weenie(0x5000_00BC));
    assert_eq!(f.get(0x5000_00BC).material_type, Some(77));
}

#[test]
fn ingest_ammo_type_preserves_wire_value() {
    let mut f = Fixture::new();
    f.ingest(WeenieData {
        name: Some("Bow".into()),
        item_type: Some(ItemType::MISSILE_WEAPON),
        combat_use: Some(2),
        ammo_type: Some(1),
        ..full_weenie(0x5000_00BA)
    });
    assert_eq!(f.get(0x5000_00BA).combat_use, Some(2));
    assert_eq!(f.get(0x5000_00BA).ammo_type, Some(1));
}

#[test]
fn ingest_radar_metadata_patches_only_present_wire_fields() {
    let mut f = Fixture::new();
    f.ingest(WeenieData {
        radar_blip_color: Some(4),
        radar_behavior: Some(3),
        ..full_weenie(0x5000_00B8)
    });
    f.ingest(WeenieData {
        radar_blip_color: None,
        radar_behavior: Some(4),
        ..full_weenie(0x5000_00B8)
    });
    assert_eq!(f.get(0x5000_00B8).radar_blip_color, Some(4));
    assert_eq!(f.get(0x5000_00B8).radar_behavior, Some(4));
}

#[test]
fn record_membership_creates_entry_and_sets_equip() {
    let mut f = Fixture::new();
    f.t.record_membership(
        0x5000_00B4,
        0,
        EquipMask::MELEE_WEAPON,
        None,
        None,
        &mut f.ev,
    );
    assert_eq!(
        f.get(0x5000_00B4).currently_equipped_location,
        EquipMask::MELEE_WEAPON
    );
    assert_eq!(f.get(0x5000_00B4).icon_id, 0);
}

#[test]
fn record_membership_owned_equip_uses_same_index_shape_as_live_wield() {
    let weapon = 0x5000_00B9;
    let mut f = Fixture::new();
    f.t.record_membership(
        weapon,
        PLAYER,
        EquipMask::MISSILE_WEAPON,
        None,
        Some(7),
        &mut f.ev,
    );
    let item = f.get(weapon);
    assert_eq!(item.container_id, PLAYER);
    assert_eq!(item.container_slot, -1);
    assert_eq!(item.currently_equipped_location, EquipMask::MISSILE_WEAPON);
    assert_eq!(item.priority, 7);
    assert_eq!(f.contents(PLAYER), vec![weapon]);
}

#[test]
fn ingest_after_membership_fills_data_without_duplicate() {
    let mut f = Fixture::new();
    f.t.record_membership(0x5000_00B5, 0, EquipMask::NONE, None, None, &mut f.ev);
    f.ingest(full_weenie(0x5000_00B5));
    assert_eq!(f.t.object_count(), 1);
    assert_eq!(f.get(0x5000_00B5).icon_id, 0x0600_1234);
}

#[test]
fn membership_after_ingest_preserves_data_without_duplicate() {
    let mut f = Fixture::new();
    f.ingest(full_weenie(0x5000_00B6));
    f.t.record_membership(
        0x5000_00B6,
        0,
        EquipMask::MELEE_WEAPON,
        None,
        None,
        &mut f.ev,
    );
    assert_eq!(f.t.object_count(), 1);
    assert_eq!(f.get(0x5000_00B6).icon_id, 0x0600_1234);
    assert_eq!(
        f.get(0x5000_00B6).currently_equipped_location,
        EquipMask::MELEE_WEAPON
    );
}

#[test]
fn record_membership_inventory_entry_clears_stale_equip_location() {
    let mut f = Fixture::new();
    let mut item = ClientObject::new(0x5000_00B8);
    item.currently_equipped_location = EquipMask::CHEST_WEAR | EquipMask::UPPER_ARM_WEAR;
    f.add(item);
    f.t.record_membership(0x5000_00B8, 0, EquipMask::NONE, None, None, &mut f.ev);
    assert_eq!(
        f.get(0x5000_00B8).currently_equipped_location,
        EquipMask::NONE
    );
}

#[test]
fn container_index_ingest_then_contents_ordered_by_slot() {
    let mut f = Fixture::new();
    f.ingest(in_container(0x510, 0xC0));
    f.ingest(in_container(0x511, 0xC0));
    f.mv(0x510, 0xC0, 1);
    f.mv(0x511, 0xC0, 0);
    assert_eq!(f.contents(0xC0), vec![0x511, 0x510]);
}

#[test]
fn container_index_move_reparents_between_containers() {
    let mut f = Fixture::new();
    f.ingest(in_container(0x520, 0xC1));
    f.mv(0x520, 0xC2, 0);
    assert!(f.contents(0xC1).is_empty());
    assert_eq!(f.contents(0xC2), vec![0x520]);
}

#[test]
fn container_index_remove_drops_from_contents() {
    let mut f = Fixture::new();
    f.ingest(in_container(0x530, 0xC3));
    f.t.remove(0x530, &mut f.ev);
    assert!(f.contents(0xC3).is_empty());
}

#[test]
fn get_contents_unknown_container_is_empty() {
    assert!(ClientObjectTable::new().get_contents(0xDEAD).is_empty());
}

#[test]
fn ingest_creature_typed_resolves_name_and_type() {
    let mut f = Fixture::new();
    f.ingest(WeenieData {
        name: Some("Drudge".into()),
        item_type: Some(ItemType::CREATURE),
        ..full_weenie(0x560)
    });
    assert_eq!(f.get(0x560).name, "Drudge");
    assert!(f.get(0x560).item_type.intersects(ItemType::CREATURE));
}

#[test]
fn replace_contents_records_all_in_list_order() {
    let mut f = Fixture::new();
    f.t.replace_contents_ids(0xC9, &[0xA01, 0xA02, 0xA03], &mut f.ev);
    assert_eq!(f.contents(0xC9), vec![0xA01, 0xA02, 0xA03]);
    assert_eq!(f.get(0xA01).container_id, 0);
}

#[test]
fn replace_contents_publishes_list_replacement_without_synthetic_move() {
    let mut f = Fixture::new();
    f.t.replace_contents_ids(0xC9, &[0xA01], &mut f.ev);
    let events = f.take();
    assert!(moves(&events).is_empty());
    assert_eq!(replaced(&events), vec![0xC9]);
}

#[test]
fn projection_only_member_authoritative_move_evicts_stale_viewed_list() {
    let (chest, pack, item) = (0x4000_0001, 0x5000_0001, 0xA20);
    let mut f = Fixture::new();
    f.t.replace_contents_ids(chest, &[item], &mut f.ev);
    assert!(f.server_move(item, pack, 0));
    assert!(f.contents(chest).is_empty());
    assert_eq!(f.contents(pack), vec![item]);
}

fn arrow_bought(guid: u32, pack: u32) -> WeenieData {
    WeenieData {
        guid,
        name: Some("Arrow".into()),
        item_type: Some(ItemType::MISSILE_WEAPON),
        weenie_class_id: 5,
        value: Some(100),
        stack_size: Some(100),
        burden: Some(1),
        container_id: Some(pack),
        wielder_id: Some(0),
        ..WeenieData::default()
    }
}

#[test]
fn buy_shaped_fresh_guid_create_then_placement_zero_echo_inserts_at_list_head() {
    let (pack, a, b, bought) = (0x5000_0001, 0xA01, 0xA02, 0xB01);
    let mut f = Fixture::new();
    f.t.initialize_inventory_manifest(pack, &[entry(a, 0), entry(b, 0)], &mut f.ev);
    f.ingest(arrow_bought(bought, pack));
    assert!(f.confirmed_move(bought, pack, 0, 0));
    assert_eq!(f.contents(pack), vec![bought, a, b]);
}

#[test]
fn contain_id_arriving_before_create_object_still_inserts_at_list_head() {
    let (pack, a, b, bought) = (0x5000_0001, 0xA01, 0xA02, 0xB02);
    let mut f = Fixture::new();
    f.t.initialize_inventory_manifest(pack, &[entry(a, 0), entry(b, 0)], &mut f.ev);
    // The placement arrives first: nothing to move yet, but it is kept.
    assert!(!f.confirmed_move(bought, pack, 0, 0));
    f.ingest(arrow_bought(bought, pack));
    assert_eq!(f.contents(pack), vec![bought, a, b]);
    assert_eq!(f.get(bought).container_id, pack);
    assert_eq!(f.get(bought).container_type_hint, 0);
}

#[test]
fn a_kept_placement_is_not_laid_over_a_description_that_places_the_object_elsewhere() {
    // Seen live: the giver's record goes with InventoryRemoveObject, the server then
    // names the receiver as its container, and the receiver's wield later
    // describes it again with a wielder and no container. Retail records
    // an unknown item's containment on the container, never on the item,
    // so the description stands.
    let (receiver, wand) = (0x5000_000B, 0x8000_DB28);
    let mut f = Fixture::new();
    assert!(!f.confirmed_move(wand, receiver, 0, 0));
    f.ingest(WeenieData {
        guid: wand,
        name: Some("Wand".into()),
        item_type: Some(ItemType::CASTER),
        weenie_class_id: 9,
        wielder_id: Some(receiver),
        current_wielded_location: Some(EquipMask::HELD),
        ..WeenieData::default()
    });
    let item = f.get(wand);
    assert_eq!(item.container_id, 0);
    assert_eq!(item.wielder_id, receiver);
    assert_eq!(item.currently_equipped_location, EquipMask::HELD);
    assert!(f.contents(receiver).is_empty());
    assert_eq!(f.equipped_ids(receiver), [wand]);
}

#[test]
fn pending_unresolved_placement_is_dropped_by_clear() {
    let (pack, never_arrives) = (0x5000_0001, 0xB03);
    let mut f = Fixture::new();
    assert!(!f.confirmed_move(never_arrives, pack, 0, 0));
    f.t.clear(&mut f.ev);
    f.ingest(WeenieData {
        guid: never_arrives,
        name: Some("Something Else".into()),
        item_type: Some(ItemType::MISC),
        weenie_class_id: 9,
        value: Some(1),
        burden: Some(1),
        container_id: Some(pack),
        wielder_id: Some(0),
        ..WeenieData::default()
    });
    assert_eq!(f.contents(pack), vec![never_arrives]);
}

#[test]
fn authoritative_pickup_placement_zero_inserts_at_list_head() {
    let (corpse, pack, a, b, first, second) =
        (0x4000_0001, 0x5000_0001, 0xA01, 0xA02, 0xA03, 0xA04);
    let mut f = Fixture::new();
    f.t.initialize_inventory_manifest(pack, &[entry(a, 0), entry(b, 0)], &mut f.ev);
    f.t.replace_contents_ids(corpse, &[first, second], &mut f.ev);

    assert!(f.confirmed_move(first, pack, 0, 0));
    assert_eq!(f.contents(pack), vec![first, a, b]);
    let slots: Vec<i32> = f
        .contents(pack)
        .iter()
        .map(|&id| f.get(id).container_slot)
        .collect();
    assert_eq!(slots, vec![0, 1, 2]);

    assert!(f.confirmed_move(second, pack, 0, 0));
    assert_eq!(f.contents(pack), vec![second, first, a, b]);
    let slots: Vec<i32> = f
        .contents(pack)
        .iter()
        .map(|&id| f.get(id).container_slot)
        .collect();
    assert_eq!(slots, vec![0, 1, 2, 3]);
    assert!(f.contents(corpse).is_empty());
}

#[test]
fn authoritative_container_move_indexes_only_the_container_list() {
    let (pack, loose_a, loose_b, bag_a, bag_b) = (0x5000_0001, 0xA11, 0xA12, 0xA21, 0xA22);
    let mut f = Fixture::new();
    f.t.initialize_inventory_manifest(
        pack,
        &[entry(loose_a, 0), entry(bag_a, 1), entry(loose_b, 0)],
        &mut f.ev,
    );
    let mut bag = ClientObject::new(bag_b);
    bag.item_type = ItemType::CONTAINER;
    bag.items_capacity = 24;
    f.add(bag);

    assert!(f.confirmed_move(bag_b, pack, 0, 1));

    let is_bag = |f: &Fixture, id: u32| {
        let item = f.get(id);
        item.container_type_hint != 0 || item.item_type.intersects(ItemType::CONTAINER)
    };
    let loose: Vec<u32> = f
        .contents(pack)
        .into_iter()
        .filter(|&id| !is_bag(&f, id))
        .collect();
    let bags: Vec<u32> = f
        .contents(pack)
        .into_iter()
        .filter(|&id| is_bag(&f, id))
        .collect();
    assert_eq!(loose, vec![loose_a, loose_b]);
    assert_eq!(bags, vec![bag_b, bag_a]);
    assert_eq!(f.get(bag_b).container_slot, 0);
    assert_eq!(f.get(bag_a).container_slot, 1);
}

#[test]
fn projection_eviction_follow_up_move_sees_committed_state() {
    let (chest, pack_a, pack_b, item) = (0x4000_0001, 0x5000_0001, 0x5000_0002, 0xA22);
    let mut f = Fixture::new();
    f.t.replace_contents_ids(chest, &[item], &mut f.ev);
    f.take();
    assert!(f.server_move(item, pack_a, 0));
    if replaced(&f.take()).contains(&chest) {
        assert!(f.server_move(item, pack_b, 0));
    }
    assert!(f.contents(chest).is_empty());
    assert!(f.contents(pack_a).is_empty());
    assert_eq!(f.contents(pack_b), vec![item]);
    assert_eq!(f.get(item).container_id, pack_b);
}

#[test]
fn projection_only_member_delete_evicts_stale_viewed_list_and_notifies() {
    let (chest, item) = (0x4000_0001, 0xA21);
    let mut f = Fixture::new();
    f.t.replace_contents_ids(chest, &[item], &mut f.ev);
    f.take();
    assert!(f.t.remove(item, &mut f.ev));
    assert!(f.contents(chest).is_empty());
    assert_eq!(replaced(&f.take()), vec![chest]);
}

#[test]
fn delete_projection_follow_up_guid_reuse_survives_completed_teardown() {
    let (chest, item) = (0x4000_0001, 0xA23);
    let mut f = Fixture::new();
    f.t.replace_contents_ids(chest, &[item], &mut f.ev);
    f.take();
    assert!(f.t.remove(item, &mut f.ev));
    if replaced(&f.take()).contains(&chest) {
        let mut reused = ClientObject::new(item);
        reused.wielder_id = PLAYER;
        reused.currently_equipped_location = EquipMask::MISSILE_WEAPON;
        f.add(reused);
    }
    assert!(f.t.get(item).is_some());
    assert_eq!(f.equipped_ids(PLAYER), vec![item]);
}

#[test]
fn replace_contents_second_smaller_list_replaces_projection_only() {
    let mut f = Fixture::new();
    f.t.replace_contents_ids(0xC9, &[0xA01, 0xA02, 0xA03], &mut f.ev);
    f.t.replace_contents_ids(0xC9, &[0xA02], &mut f.ev);
    assert_eq!(f.contents(0xC9), vec![0xA02]);
    for id in [0xA01, 0xA02, 0xA03] {
        assert_eq!(f.get(id).container_id, 0);
    }
}

#[test]
fn replace_contents_listed_member_preserves_canonical_placement() {
    let mut f = Fixture::new();
    let mut item = ClientObject::new(0xA10);
    item.wielder_id = PLAYER;
    item.currently_equipped_location = EquipMask::CHEST_WEAR | EquipMask::UPPER_ARM_WEAR;
    f.add(item);
    f.t.replace_contents_ids(PLAYER, &[0xA10], &mut f.ev);
    let item = f.get(0xA10);
    assert_eq!(item.container_id, 0);
    assert_eq!(item.wielder_id, PLAYER);
    assert_eq!(item.container_slot, -1);
    assert_eq!(
        item.currently_equipped_location,
        EquipMask::CHEST_WEAR | EquipMask::UPPER_ARM_WEAR
    );
}

#[test]
fn replace_contents_omitted_member_preserves_canonical_placement() {
    let mut f = Fixture::new();
    let mut item = ClientObject::new(0xA11);
    item.wielder_id = PLAYER;
    f.add(item);
    f.t.move_item(
        0xA11,
        PLAYER,
        0,
        EquipMask::CHEST_WEAR | EquipMask::UPPER_ARM_WEAR,
        None,
        &mut f.ev,
    );
    f.t.replace_contents_ids(PLAYER, &[], &mut f.ev);
    let item = f.get(0xA11);
    assert_eq!(item.container_id, PLAYER);
    assert_eq!(item.wielder_id, PLAYER);
    assert_eq!(
        item.currently_equipped_location,
        EquipMask::CHEST_WEAR | EquipMask::UPPER_ARM_WEAR
    );
}

#[test]
fn replace_contents_reorders_to_new_list_order() {
    let mut f = Fixture::new();
    f.t.replace_contents_ids(0xC9, &[0xA01, 0xA02], &mut f.ev);
    f.t.replace_contents_ids(0xC9, &[0xA02, 0xA01], &mut f.ev);
    assert_eq!(f.contents(0xC9), vec![0xA02, 0xA01]);
}

#[test]
fn replace_contents_records_container_type_hint_and_order() {
    let mut f = Fixture::new();
    f.t.replace_contents(0xC9, &[entry(0xA02, 1), entry(0xA01, 0)], &mut f.ev);
    assert_eq!(f.contents(0xC9), vec![0xA02, 0xA01]);
    assert_eq!(f.get(0xA02).container_type_hint, 1);
    assert_eq!(f.get(0xA01).container_type_hint, 0);
}

// GetContainedItems_leaves_out_packs_and_foci_in_listing_order
// (OpenAC issue #221).
#[test]
fn get_contained_items_leaves_out_packs_and_foci_in_listing_order() {
    let mut f = Fixture::new();
    f.t.replace_contents(
        0xC0,
        &[
            entry(0x540, 0),
            entry(0x541, 1),
            entry(0x542, 2),
            entry(0x543, 0),
        ],
        &mut f.ev,
    );
    let mut pack = ClientObject::new(0x544);
    pack.item_type = ItemType::CONTAINER;
    f.add(pack);
    f.mv(0x544, 0xC0, 0);
    assert_eq!(f.t.get_contained_items(0xC0), vec![0x540, 0x543]);
    assert!(f.t.get_contained_items(0xC9).is_empty());
}

#[test]
fn replace_contents_manifest_order_survives_later_create_objects() {
    let mut f = Fixture::new();
    f.t.replace_contents(PLAYER, &[entry(0xA01, 0), entry(0xA02, 1)], &mut f.ev);
    f.ingest(WeenieData {
        item_type: Some(ItemType::CONTAINER),
        ..in_container(0xA02, PLAYER)
    });
    f.ingest(in_container(0xA01, PLAYER));
    assert_eq!(f.contents(PLAYER), vec![0xA01, 0xA02]);
    assert_eq!(f.get(0xA01).container_slot, -1);
    assert_eq!(f.get(0xA02).container_slot, -1);
    assert_eq!(f.get(0xA02).container_type_hint, 1);
}

// OpenAC v0.1.25 `tests/AcDream.Core.Tests/Items/
// InventoryCompletenessTests.cs`.
#[test]
fn an_optimistic_drop_does_not_prove_ownership_loss_until_confirmed() {
    // OptimisticDropDoesNotProveOwnershipLossUntilConfirmed.
    let mut f = Fixture::new();
    f.t.initialize_inventory_manifest(1, &[entry(2, 0)], &mut f.ev);
    assert!(f.t.has_complete_inventory(1));
    f.t.move_item_optimistic(2, 0, -1, &mut f.ev);
    assert!(!f.t.has_complete_inventory(1));
    f.t.rollback_move(2, &mut f.ev);
    assert!(f.t.has_complete_inventory(1));
    f.t.move_item_optimistic(2, 0, -1, &mut f.ev);
    f.t.confirm_move(2);
    assert!(f.t.has_complete_inventory(1));
    assert!(f.t.get_contents(1).is_empty());
}

#[test]
fn login_waits_for_nested_listings_and_a_clear_revokes_completeness() {
    // LoginWaitsForNestedListingsAndClearRevokesCompleteness and
    // EmptyAuthoritativeInventoryIsComplete.
    let mut f = Fixture::new();
    assert!(!f.t.has_complete_inventory(1));
    f.t.initialize_inventory_manifest(1, &[entry(2, 1)], &mut f.ev);
    assert!(!f.t.has_complete_inventory(1));
    f.t.replace_contents(2, &[entry(3, 1)], &mut f.ev);
    assert!(!f.t.has_complete_inventory(1));
    f.t.replace_contents_ids(3, &[], &mut f.ev);
    assert!(f.t.has_complete_inventory(1));
    // Closing a nested pack's view drops its listing.
    f.t.stop_viewing_contents(3, &mut f.ev);
    assert!(!f.t.has_complete_inventory(1));
    f.t.replace_contents_ids(3, &[], &mut f.ev);
    f.t.clear(&mut f.ev);
    assert!(!f.t.has_complete_inventory(1));
    f.t.initialize_inventory_manifest(1, &[], &mut f.ev);
    assert!(f.t.has_complete_inventory(1));
}

// OpenAC main (PR #226) `InventoryCompletenessTests.SpellFocusNeverListedDoesNotBlockCompleteness`.
#[test]
fn a_spell_focus_never_listed_does_not_block_completeness() {
    // A focus (container type 2) takes a container slot, but the server
    // never sends its contents, since it holds nothing.
    let mut f = Fixture::new();
    f.t.initialize_inventory_manifest(1, &[entry(2, 1), entry(3, 2)], &mut f.ev);
    assert!(!f.t.has_complete_inventory(1));
    f.t.replace_contents_ids(2, &[], &mut f.ev);
    assert!(f.t.has_complete_inventory(1));
}

#[test]
fn initialize_inventory_manifest_seeds_canonical_ownership_and_order() {
    let mut f = Fixture::new();
    f.t.initialize_inventory_manifest(PLAYER, &[entry(0xA01, 0), entry(0xA02, 1)], &mut f.ev);
    assert_eq!(f.contents(PLAYER), vec![0xA01, 0xA02]);
    assert_eq!(f.get(0xA01).container_id, PLAYER);
    assert_eq!(f.get(0xA01).container_slot, 0);
    assert_eq!(f.get(0xA02).container_id, PLAYER);
    assert_eq!(f.get(0xA02).container_slot, 1);
}

fn manifest_entry(guid: u32, location: EquipMask, priority: u32) -> EquipmentManifestEntry {
    EquipmentManifestEntry {
        guid,
        equip_location: location,
        priority,
    }
}

#[test]
fn initialize_equipment_manifest_preserves_packed_order_and_canonical_ownership() {
    let mut f = Fixture::new();
    f.t.initialize_equipment_manifest(
        PLAYER,
        &[
            manifest_entry(0xA11, EquipMask::MELEE_WEAPON, 1),
            manifest_entry(0xA12, EquipMask::MISSILE_AMMO, 2),
        ],
        &mut f.ev,
    );
    assert_eq!(f.equipped_ids(PLAYER), vec![0xA11, 0xA12]);
    assert_eq!(f.get(0xA11).container_id, 0);
    assert_eq!(f.get(0xA11).wielder_id, PLAYER);
    assert!(f.contents(PLAYER).is_empty());
}

#[test]
fn initialize_equipment_manifest_replacement_clears_omitted_canonical_equipment() {
    let weapon = 0xA11;
    let mut f = Fixture::new();
    f.t.initialize_equipment_manifest(
        PLAYER,
        &[manifest_entry(weapon, EquipMask::MELEE_WEAPON, 1)],
        &mut f.ev,
    );
    f.t.get_mut(weapon).expect("present").burden = 10;
    f.t.initialize_equipment_manifest(PLAYER, &[], &mut f.ev);
    let item = f.get(weapon);
    assert_eq!(item.wielder_id, 0);
    assert_eq!(item.container_id, 0);
    assert_eq!(item.container_slot, -1);
    assert_eq!(item.currently_equipped_location, EquipMask::NONE);
    assert!(f.equipped_ids(PLAYER).is_empty());
    assert_eq!(f.t.sum_carried_burden(PLAYER), 0);
}

#[test]
fn initialize_inventory_manifest_replacement_clears_omitted_canonical_ownership() {
    let item_id = 0xA01;
    let mut f = Fixture::new();
    f.t.initialize_inventory_manifest(PLAYER, &[entry(item_id, 0)], &mut f.ev);
    f.t.get_mut(item_id).expect("present").burden = 10;
    f.t.initialize_inventory_manifest(PLAYER, &[], &mut f.ev);
    let item = f.get(item_id);
    assert_eq!(item.container_id, 0);
    assert_eq!(item.container_slot, -1);
    assert_eq!(item.wielder_id, 0);
    assert_eq!(item.currently_equipped_location, EquipMask::NONE);
    assert!(f.contents(PLAYER).is_empty());
    assert_eq!(f.t.sum_carried_burden(PLAYER), 0);
}

#[test]
fn player_description_manifests_move_item_between_inventory_and_equipment() {
    let weapon = 0xA11;
    let mut f = Fixture::new();
    f.t.initialize_inventory_manifest(PLAYER, &[entry(weapon, 0)], &mut f.ev);
    f.t.initialize_equipment_manifest(
        PLAYER,
        &[manifest_entry(weapon, EquipMask::MELEE_WEAPON, 1)],
        &mut f.ev,
    );
    assert!(f.contents(PLAYER).is_empty());
    assert_eq!(f.get(weapon).wielder_id, PLAYER);
    assert_eq!(
        f.get(weapon).currently_equipped_location,
        EquipMask::MELEE_WEAPON
    );

    f.t.initialize_equipment_manifest(PLAYER, &[], &mut f.ev);
    f.t.initialize_inventory_manifest(PLAYER, &[entry(weapon, 0)], &mut f.ev);
    assert!(f.equipped_ids(PLAYER).is_empty());
    assert_eq!(f.contents(PLAYER), vec![weapon]);
    assert_eq!(f.get(weapon).container_id, PLAYER);
    assert_eq!(f.get(weapon).wielder_id, 0);
}

#[test]
fn reindex_unknown_slot_appends_after_known_snapshot_slots() {
    let pack = 0x5000_00C9;
    let mut f = Fixture::new();
    f.t.replace_contents_ids(pack, &[0xA01, 0xA02], &mut f.ev);
    f.ingest(in_container(0xA03, pack));
    assert_eq!(f.contents(pack), vec![0xA01, 0xA02, 0xA03]);
    assert_eq!(f.get(0xA03).container_slot, -1);
}

#[test]
fn replace_contents_zero_container_is_a_no_op() {
    let mut f = Fixture::new();
    f.t.replace_contents_ids(0, &[0xA01], &mut f.ev);
    assert!(f.contents(0).is_empty());
    assert!(f.t.get(0xA01).is_none());
}

#[test]
fn move_item_optimistic_moves_and_remembers_pre_move() {
    let mut f = Fixture::new();
    f.ingest(in_container(0x700, 0xC0));
    f.mv(0x700, 0xC0, 3);
    f.t.move_item_optimistic(0x700, 0xC1, 0, &mut f.ev);
    assert_eq!(f.get(0x700).container_id, 0xC1);
    assert!(f.t.rollback_move(0x700, &mut f.ev));
    assert_eq!(f.get(0x700).container_id, 0xC0);
    assert_eq!(f.get(0x700).container_slot, 3);
}

#[test]
fn confirm_move_clears_pending_so_rollback_is_a_no_op() {
    let mut f = Fixture::new();
    f.ingest(in_container(0x701, 0xC0));
    f.t.move_item_optimistic(0x701, 0xC1, 0, &mut f.ev);
    f.t.confirm_move(0x701);
    assert!(!f.t.rollback_move(0x701, &mut f.ev));
    assert_eq!(f.get(0x701).container_id, 0xC1);
}

#[test]
fn move_item_optimistic_twice_keeps_the_original_pre_move() {
    let mut f = Fixture::new();
    f.ingest(in_container(0x702, 0xC0));
    f.t.move_item_optimistic(0x702, 0xC1, 0, &mut f.ev);
    f.t.move_item_optimistic(0x702, 0xC2, 0, &mut f.ev);
    f.t.rollback_move(0x702, &mut f.ev);
    assert_eq!(f.get(0x702).container_id, 0xC0);
}

#[test]
fn rollback_move_unknown_item_is_false() {
    let mut f = Fixture::new();
    assert!(!f.t.rollback_move(0xDEAD, &mut f.ev));
}

#[test]
fn move_item_optimistic_twice_one_confirm_still_rolls_back() {
    let mut f = Fixture::new();
    f.ingest(in_container(0x910, 0xC0));
    f.mv(0x910, 0xC0, 5);
    f.t.move_item_optimistic(0x910, 0xC1, 0, &mut f.ev);
    f.t.move_item_optimistic(0x910, 0xC2, 0, &mut f.ev);
    f.t.confirm_move(0x910);
    assert!(f.t.rollback_move(0x910, &mut f.ev));
    assert_eq!(f.get(0x910).container_id, 0xC0);
    assert_eq!(f.get(0x910).container_slot, 5);
}

#[test]
fn clear_drops_pending_moves_so_recycled_guid_is_not_rolled_back() {
    let mut f = Fixture::new();
    f.ingest(in_container(0x911, 0xC0));
    f.t.move_item_optimistic(0x911, 0xC1, 0, &mut f.ev);
    f.t.clear(&mut f.ev);
    f.ingest(in_container(0x911, 0xC5));
    assert!(!f.t.rollback_move(0x911, &mut f.ev));
    assert_eq!(f.get(0x911).container_id, 0xC5);
}

#[test]
fn move_item_optimistic_insert_before_shifts_others_and_keeps_gapless_order() {
    let mut f = Fixture::new();
    for (slot, id) in [0x920, 0x921, 0x922].into_iter().enumerate() {
        f.ingest(in_container(id, 0xC0));
        f.mv(id, 0xC0, slot as i32);
    }
    f.t.move_item_optimistic(0x922, 0xC0, 0, &mut f.ev);
    assert_eq!(f.contents(0xC0), vec![0x922, 0x920, 0x921]);
    assert_eq!(f.get(0x922).container_slot, 0);
    assert_eq!(f.get(0x920).container_slot, 1);
    assert_eq!(f.get(0x921).container_slot, 2);
}

#[test]
fn container_index_slot_change_resorts_in_place() {
    let mut f = Fixture::new();
    f.ingest(in_container(0x540, 0xC4));
    f.ingest(in_container(0x541, 0xC4));
    f.mv(0x540, 0xC4, 0);
    f.mv(0x541, 0xC4, 1);
    assert_eq!(f.contents(0xC4), vec![0x540, 0x541]);
    f.mv(0x540, 0xC4, 5);
    assert_eq!(f.contents(0xC4), vec![0x541, 0x540]);
}

#[test]
fn wield_item_optimistic_equips_instantly() {
    let pack = 0x4000_0005;
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x940));
    f.mv(0x940, pack, 2);
    f.t.wield_item_optimistic(0x940, PLAYER, EquipMask::HEAD_WEAR, &mut f.ev);
    let item = f.get(0x940);
    assert_eq!(item.currently_equipped_location, EquipMask::HEAD_WEAR);
    assert_eq!(item.container_id, PLAYER);
    assert_eq!(item.wielder_id, 0);
}

#[test]
fn wield_item_optimistic_rollback_unequips_and_returns_to_pack() {
    let pack = 0x4000_0005;
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x941));
    f.mv(0x941, pack, 2);
    f.t.wield_item_optimistic(0x941, PLAYER, EquipMask::HEAD_WEAR, &mut f.ev);
    assert!(f.t.rollback_move(0x941, &mut f.ev));
    let item = f.get(0x941);
    assert_eq!(item.currently_equipped_location, EquipMask::NONE);
    assert_eq!(item.container_id, pack);
    assert_eq!(item.container_slot, 2);
}

#[test]
fn rollback_move_restores_pre_move_equip_location() {
    let mut f = Fixture::new();
    let identity = f.add(ClientObject::new(0x942));
    f.t.move_item(0x942, PLAYER, -1, EquipMask::SHIELD, None, &mut f.ev);
    f.t.move_item_optimistic(0x942, PLAYER, 0, &mut f.ev);
    assert_eq!(f.get(0x942).currently_equipped_location, EquipMask::NONE);
    f.take();
    assert!(f.t.rollback_move(0x942, &mut f.ev));
    assert_eq!(f.get(0x942).currently_equipped_location, EquipMask::SHIELD);
    assert!(f.take().contains(&TableEvent::MoveRolledBack {
        object_id: 0x942,
        identity
    }));
}

#[test]
fn wield_item_optimistic_unknown_item_is_false() {
    let mut f = Fixture::new();
    assert!(!f.t.wield_item_optimistic(0xDEAD, 1, EquipMask::HEAD_WEAR, &mut f.ev));
}

#[test]
fn apply_server_move_updates_authoritative_wielder_across_wield_and_unwield() {
    let (item_id, pack) = (0x9430, 0x4000_0005);
    let mut f = Fixture::new();
    let mut item = ClientObject::new(item_id);
    item.container_id = PLAYER;
    item.wielder_id = PLAYER;
    item.currently_equipped_location = EquipMask::MISSILE_WEAPON;
    f.add(item);

    assert!(f.server_move(item_id, pack, 2));
    assert_eq!(f.get(item_id).container_id, pack);
    assert_eq!(f.get(item_id).wielder_id, 0);
    assert_eq!(f.get(item_id).currently_equipped_location, EquipMask::NONE);

    assert!(f.t.apply_server_move(
        item_id,
        wielded(PLAYER, EquipMask::MISSILE_WEAPON),
        None,
        &mut f.ev
    ));
    assert_eq!(f.get(item_id).container_id, 0);
    assert_eq!(f.get(item_id).wielder_id, PLAYER);
    assert_eq!(
        f.get(item_id).currently_equipped_location,
        EquipMask::MISSILE_WEAPON
    );
}

#[test]
fn confirmed_server_wield_follow_up_move_keeps_new_pending_request() {
    let (item_id, pack) = (0x9431, 0x4000_0005);
    let mut f = Fixture::new();
    let mut item = ClientObject::new(item_id);
    item.container_id = pack;
    item.container_slot = 0;
    f.add(item);
    assert!(f.t.wield_item_optimistic(item_id, PLAYER, EquipMask::MISSILE_WEAPON, &mut f.ev));
    f.take();

    assert!(f.t.apply_confirmed_server_wield(
        item_id,
        PLAYER,
        EquipMask::MISSILE_WEAPON,
        &mut f.ev
    ));
    let wielded_now = moves(&f.take())
        .iter()
        .any(|m| m.current.wielder_id == PLAYER);
    assert!(wielded_now);
    assert!(f.t.move_item_optimistic(item_id, pack, 0, &mut f.ev));

    assert!(f.t.rollback_move(item_id, &mut f.ev));
    assert_eq!(f.get(item_id).container_id, 0);
    assert_eq!(f.get(item_id).wielder_id, PLAYER);
    assert_eq!(
        f.get(item_id).currently_equipped_location,
        EquipMask::MISSILE_WEAPON
    );
}

#[test]
fn get_equipped_by_preserves_head_insertion_order() {
    let mut f = Fixture::new();
    let mut a = ClientObject::new(0x9432);
    a.wielder_id = PLAYER;
    a.currently_equipped_location = EquipMask::MISSILE_WEAPON;
    f.add(a);
    let mut b = ClientObject::new(0x9433);
    b.container_id = PLAYER;
    b.currently_equipped_location = EquipMask::MISSILE_AMMO;
    f.add(b);
    let mut c = ClientObject::new(0x9434);
    c.container_id = PLAYER;
    f.add(c);
    assert_eq!(f.equipped_ids(PLAYER), vec![0x9433, 0x9432]);
}

#[test]
fn reject_move_without_optimistic_mutation_still_publishes_server_failure() {
    let mut f = Fixture::new();
    assert!(!f.t.reject_move(0xDEAD, 0x04FF, &mut f.ev));
    assert_eq!(
        f.take(),
        vec![TableEvent::MoveRequestFailed(MoveRequestFailure {
            item_id: 0xDEAD,
            weenie_error: 0x04FF,
            rolled_back: false
        })]
    );
}

#[test]
fn reject_move_with_optimistic_mutation_rolls_back_and_publishes_failure() {
    let (item_id, pack) = (0x943, 0x4000_0005);
    let mut f = Fixture::new();
    f.add(ClientObject::new(item_id));
    f.mv(item_id, pack, 2);
    f.t.wield_item_optimistic(item_id, PLAYER, EquipMask::MELEE_WEAPON, &mut f.ev);
    f.take();

    assert!(f.t.reject_move(item_id, 0x04FF, &mut f.ev));

    assert_eq!(f.get(item_id).container_id, pack);
    assert_eq!(f.get(item_id).currently_equipped_location, EquipMask::NONE);
    assert_eq!(
        f.take().last(),
        Some(&TableEvent::MoveRequestFailed(MoveRequestFailure {
            item_id,
            weenie_error: 0x04FF,
            rolled_back: true
        }))
    );
}

#[test]
fn confirmed_server_wield_publishes_independently_of_rollback_outstanding_count() {
    let (item_id, pack) = (0x944, 0x4000_0005);
    let mut f = Fixture::new();
    f.add(ClientObject::new(item_id));
    f.mv(item_id, pack, 2);
    f.t.wield_item_optimistic(item_id, PLAYER, EquipMask::MELEE_WEAPON, &mut f.ev);
    f.t.move_item_optimistic(item_id, pack, 0, &mut f.ev);
    f.take();

    assert!(f.t.apply_confirmed_server_wield(item_id, PLAYER, EquipMask::MELEE_WEAPON, &mut f.ev));

    let confirmed: Vec<u32> = f
        .take()
        .iter()
        .filter_map(|event| match event {
            TableEvent::WieldConfirmed(id) => Some(*id),
            _ => None,
        })
        .collect();
    assert_eq!(confirmed, vec![item_id]);
    assert_eq!(f.get(item_id).container_id, 0);
    assert_eq!(f.get(item_id).wielder_id, PLAYER);
    assert!(f.t.rollback_move(item_id, &mut f.ev));
    assert_eq!(f.get(item_id).container_id, pack);
    assert_eq!(f.get(item_id).wielder_id, 0);
    assert_eq!(f.get(item_id).currently_equipped_location, EquipMask::NONE);
}

#[test]
fn wield_then_move_one_confirm_rolls_back_to_pre_wield() {
    let (pack, pack2) = (0x4000_0005, 0x4000_0006);
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x950));
    f.mv(0x950, pack, 2);
    f.t.wield_item_optimistic(0x950, PLAYER, EquipMask::HEAD_WEAR, &mut f.ev);
    f.t.move_item_optimistic(0x950, pack2, 0, &mut f.ev);
    f.t.confirm_move(0x950);
    assert!(f.t.rollback_move(0x950, &mut f.ev));
    let item = f.get(0x950);
    assert_eq!(item.container_id, pack);
    assert_eq!(item.container_slot, 2);
    assert_eq!(item.currently_equipped_location, EquipMask::NONE);
}

#[test]
fn update_int64_property_sets_value_and_reports_updated() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x960));
    f.take();
    assert!(f.t.update_int64_property(0x960, 2, 12_345, &mut f.ev));
    assert_eq!(f.get(0x960).properties.int64s[&2], 12_345);
    assert_eq!(count_updated(&f.take()), 1);
}

#[test]
fn update_int64_property_unknown_object_is_false() {
    let mut f = Fixture::new();
    assert!(!f.t.update_int64_property(0xDEAD, 2, 1, &mut f.ev));
}

// ClientObjectTableUpdateTests.cs

#[test]
fn upsert_properties_unknown_object_creates_then_merges() {
    let mut f = Fixture::new();
    let mut bundle = PropertyBundle::default();
    bundle.ints.insert(5, 1234);
    f.t.upsert_properties(PLAYER, &bundle, &mut f.ev);
    assert_eq!(f.get(PLAYER).properties.ints[&5], 1234);
    assert!(matches!(f.take()[0], TableEvent::Added { .. }));
}

#[test]
fn upsert_properties_existing_object_merges_and_reports_updated() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(PLAYER));
    f.take();
    let mut bundle = PropertyBundle::default();
    bundle.ints.insert(5, 99);
    f.t.upsert_properties(PLAYER, &bundle, &mut f.ev);
    assert_eq!(f.get(PLAYER).properties.ints[&5], 99);
    assert_eq!(count_updated(&f.take()), 1);
}

#[test]
fn update_stack_size_known_object_sets_fields_and_reports() {
    let mut f = Fixture::new();
    let mut item = ClientObject::new(0x600);
    item.value = 5;
    let identity = f.add(item);
    f.take();
    assert!(f.t.update_stack_size(0x600, 25, 125, &mut f.ev));
    assert_eq!(f.get(0x600).stack_size, 25);
    assert_eq!(f.get(0x600).value, 125);
    let events = f.take();
    assert_eq!(count_updated(&events), 1);
    assert!(events.contains(&TableEvent::StackSizeUpdated {
        object_id: 0x600,
        identity
    }));
}

#[test]
fn update_stack_size_unknown_object_is_false() {
    let mut f = Fixture::new();
    assert!(!f.t.update_stack_size(0xDEAD, 1, 1, &mut f.ev));
}

#[test]
fn update_appraisal_retains_properties_and_spell_snapshot() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x700));
    f.take();
    let mut appraisal = AppraisalUpdate {
        spell_ids: vec![1327, 1132],
        received_at_seconds: 12.345,
        ..AppraisalUpdate::default()
    };
    appraisal.properties.ints.insert(106, 420);
    assert!(f.t.update_appraisal(0x700, &appraisal, &mut f.ev));
    appraisal.spell_ids[0] = 0;

    let item = f.get(0x700);
    assert_eq!(item.properties.ints[&106], 420);
    assert_eq!(item.appraised_spell_ids, vec![1327, 1132]);
    assert_eq!(item.last_appraisal_time_ms, 12345);
    assert_eq!(count_updated(&f.take()), 1);
}

#[test]
fn update_appraisal_retains_profiles_and_they_survive_later_property_update() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x701));
    let weapon = ClientWeaponProfile {
        damage_type: 4,
        weapon_time: 30,
        weapon_skill: 34,
        damage: 12,
        damage_variance: 0.2,
        damage_mod: 1.1,
        weapon_length: 1.0,
        max_velocity: 2.0,
        weapon_offense: 1.05,
        max_velocity_estimated: 1,
    };
    let armor = ClientArmorProfile {
        slashing_protection: 1.5,
        piercing_protection: 1.4,
        bludgeoning_protection: 1.3,
        cold_protection: 1.2,
        fire_protection: 1.1,
        acid_protection: 1.0,
        nether_protection: 0.9,
        lightning_protection: 0.8,
    };
    let appraisal = AppraisalUpdate {
        weapon_profile: Some(weapon),
        armor_profile: Some(armor),
        ..AppraisalUpdate::default()
    };
    assert!(f.t.update_appraisal(0x701, &appraisal, &mut f.ev));
    assert_eq!(f.get(0x701).weapon_profile, Some(weapon));
    assert_eq!(f.get(0x701).armor_profile, Some(armor));

    let mut follow_up = PropertyBundle::default();
    follow_up.ints.insert(5, 999);
    assert!(f.t.update_properties(0x701, &follow_up, &mut f.ev));
    assert_eq!(f.get(0x701).properties.ints[&5], 999);
    assert_eq!(f.get(0x701).weapon_profile, Some(weapon));
    assert_eq!(f.get(0x701).armor_profile, Some(armor));
}

#[test]
fn update_appraisal_without_a_profile_clears_a_stale_one() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x702));
    let weapon = ClientWeaponProfile {
        damage_type: 1,
        weapon_time: 1,
        weapon_skill: 1,
        damage: 1,
        ..ClientWeaponProfile::default()
    };
    let with_weapon = AppraisalUpdate {
        weapon_profile: Some(weapon),
        ..AppraisalUpdate::default()
    };
    f.t.update_appraisal(0x702, &with_weapon, &mut f.ev);
    assert!(f.get(0x702).weapon_profile.is_some());
    f.t.update_appraisal(0x702, &AppraisalUpdate::default(), &mut f.ev);
    assert!(f.get(0x702).weapon_profile.is_none());
    assert!(f.get(0x702).armor_profile.is_none());
}

#[test]
fn notify_object_updated_publishes_a_client_side_change() {
    let mut f = Fixture::new();
    let identity = f.add(ClientObject::new(PLAYER));
    f.take();
    f.t.get_mut(PLAYER).expect("present").sell_state = 1;
    assert!(f.t.notify_object_updated(PLAYER, &mut f.ev));
    assert_eq!(
        f.take(),
        vec![TableEvent::Updated {
            object_id: PLAYER,
            identity
        }]
    );
    assert_eq!(f.get(PLAYER).sell_state, 1);
    assert!(!f.t.notify_object_updated(0x5000_0002, &mut f.ev));
}

// ClientObjectTableBurdenTests.cs

#[test]
fn sum_carried_burden_sums_pack_side_bag_and_wielded_but_not_unrelated() {
    let mut f = Fixture::new();
    let mut loose = ClientObject::new(0xA);
    loose.container_id = PLAYER;
    loose.burden = 100;
    f.add(loose);
    let mut bag = ClientObject::new(0xB);
    bag.container_id = PLAYER;
    bag.burden = 50;
    bag.item_type = ItemType::CONTAINER;
    f.add(bag);
    let mut nested = ClientObject::new(0xC);
    nested.container_id = 0xB;
    nested.burden = 30;
    f.add(nested);
    let mut held = ClientObject::new(0xD);
    held.wielder_id = PLAYER;
    held.burden = 200;
    f.add(held);
    let mut world = ClientObject::new(0xE);
    world.burden = 999;
    f.add(world);
    assert_eq!(f.t.sum_carried_burden(PLAYER), 380);
}

#[test]
fn sum_carried_burden_unknown_owner_is_zero() {
    assert_eq!(ClientObjectTable::new().sum_carried_burden(PLAYER), 0);
}

// Behaviour OpenAC's tests reach only through its network wiring.

#[test]
fn replace_generation_removes_the_old_record_and_installs_a_new_identity() {
    let mut f = Fixture::new();
    let old = f.ingest(full_weenie(0x800));
    f.take();
    let new =
        f.t.replace_generation(&full_weenie(0x800), 3, true, &mut f.ev)
            .expect("installed");
    assert_ne!(old, new);
    let events = f.take();
    assert!(matches!(
        &events[0],
        TableEvent::Removed {
            reason: RemovalReason::GenerationReplacement,
            generation: 3,
            ..
        }
    ));
    assert!(events.contains(&TableEvent::Added {
        object_id: 0x800,
        identity: new
    }));
    assert!(
        f.t.replace_generation(&full_weenie(0x800), 4, false, &mut f.ev)
            .is_none()
    );
    assert!(f.t.get(0x800).is_none());
}

#[test]
fn icon_data_ids_mirror_into_fields() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x801));
    f.t.update_data_id_property(0x801, PropertyDataId::ICON.0, 0x0600_0001, &mut f.ev);
    f.t.update_data_id_property(
        0x801,
        PropertyDataId::ICON_OVERLAY.0,
        0x0600_0002,
        &mut f.ev,
    );
    f.t.update_data_id_property(
        0x801,
        PropertyDataId::ICON_UNDERLAY.0,
        0x0600_0003,
        &mut f.ev,
    );
    let item = f.get(0x801);
    assert_eq!(
        (item.icon_id, item.icon_overlay_id, item.icon_underlay_id),
        (0x0600_0001, 0x0600_0002, 0x0600_0003)
    );
}

// OpenAC `ClientObjectTableTests.HouseOwnerInstanceIdUpdate_SetsAndClearsHouseOwnerId`:
// a purchase or abandon announces the new owner with an instance-id update;
// the entry gate reads the house owner, and its change advances the
// revision the gate's cache follows.
#[test]
fn a_house_owner_instance_id_update_sets_and_clears_the_owner() {
    let mut f = Fixture::new();
    f.ingest(full_weenie(0x802));
    assert_eq!(f.get(0x802).house_owner_id(), None);

    let before = f.t.mutation_revision();
    let owner = crate::properties::PropertyInstanceId::HOUSE_OWNER.0;
    assert!(f.t.update_instance_id_property(0x802, owner, PLAYER, &mut f.ev));
    assert_eq!(f.get(0x802).house_owner_id(), Some(PLAYER));
    // One for the owner change, one for the update.
    assert_eq!(f.t.mutation_revision(), before + 2);

    assert!(f.t.update_instance_id_property(0x802, owner, 0, &mut f.ev));
    assert_eq!(f.get(0x802).house_owner_id(), None);
}

// Swearing or breaking allegiance announces the new monarch with an
// instance-id update (Monarch, 26); the object's monarch follows it, as the
// one its description carries does, and 0 clears it. The radar's allegiance
// square and the house entry gate both read it.
#[test]
fn a_monarch_instance_id_update_sets_and_clears_the_monarch() {
    let mut f = Fixture::new();
    f.ingest(full_weenie(0x802));
    assert_eq!(f.get(0x802).monarch_id(), None);

    let before = f.t.mutation_revision();
    let monarch = crate::properties::PropertyInstanceId::MONARCH.0;
    assert!(f.t.update_instance_id_property(0x802, monarch, 0x5000_0777, &mut f.ev));
    assert_eq!(f.get(0x802).monarch_id(), Some(0x5000_0777));
    // One for the monarch change, one for the update.
    assert_eq!(f.t.mutation_revision(), before + 2);

    assert!(f.t.update_instance_id_property(0x802, monarch, 0, &mut f.ev));
    assert_eq!(f.get(0x802).monarch_id(), None);
}

#[test]
fn house_authority_changes_advance_the_mutation_revision() {
    let mut f = Fixture::new();
    f.ingest(full_weenie(0x802));
    let before = f.t.mutation_revision();
    f.ingest(WeenieData {
        house_owner_id: Some(PLAYER),
        ..full_weenie(0x802)
    });
    // One for the owner change, one for the update.
    assert_eq!(f.t.mutation_revision(), before + 2);
    assert_eq!(f.get(0x802).house_owner_id(), Some(PLAYER));
}

// OpenAC `ClientObjectTableUpdateTests.AppraisalOutcome_followsTheLatestAnswer`:
// an unsuccessful appraisal after a successful one is recorded (and
// published) though the object was already answered; a repeat refusal only
// moves its time stamp and says nothing; a successful one clears the mark
// without losing what the first answer delivered.
#[test]
fn the_appraisal_outcome_follows_the_latest_answer() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x701));
    f.take();
    let mut appraisal = AppraisalUpdate::default();
    appraisal.properties.ints.insert(19, 40);

    assert!(f.t.update_appraisal(0x701, &appraisal, &mut f.ev));
    assert!(!f.get(0x701).last_appraisal_unsuccessful);
    assert!(f.get(0x701).appraisal_answered);

    assert!(f.t.record_unsuccessful_appraisal(0x701, 0.0, &mut f.ev));
    assert!(f.get(0x701).last_appraisal_unsuccessful);
    assert_eq!(f.get(0x701).properties.ints[&19], 40);
    assert_eq!(count_updated(&f.ev), 2);

    assert!(f.t.record_unsuccessful_appraisal(0x701, 0.0, &mut f.ev));
    assert_eq!(count_updated(&f.ev), 2);

    assert!(f.t.update_appraisal(0x701, &appraisal, &mut f.ev));
    assert!(!f.get(0x701).last_appraisal_unsuccessful);
    assert!(!f.t.record_unsuccessful_appraisal(0x702, 0.0, &mut f.ev));
}

// OpenAC `ClientObjectTableUpdateTests.AnUnsuccessfulAnswer_isStampedWithTheTableClock`
// (12.5 s, then 40 s): each refusal, a repeat one too, is stamped with the
// time it arrived.
#[test]
fn an_unsuccessful_answer_is_stamped_with_its_time() {
    let mut f = Fixture::new();
    f.add(ClientObject::new(0x701));
    assert!(f.t.record_unsuccessful_appraisal(0x701, 12.5, &mut f.ev));
    assert_eq!(f.get(0x701).last_appraisal_unsuccessful_at_seconds, 12.5);
    assert!(f.t.record_unsuccessful_appraisal(0x701, 40.0, &mut f.ev));
    assert_eq!(f.get(0x701).last_appraisal_unsuccessful_at_seconds, 40.0);
}

// OpenAC `ClientObjectTableUpdateTests.AnythingTheServerSaysAboutTheObject_overturnsARefusal`
// (every statement of its `ServerStatements`): anything the server says
// about the object shows it still has it, so an earlier refusal no longer
// stands, and whoever watches the object hears that it changed.
#[test]
fn anything_the_server_says_about_the_object_overturns_a_refusal() {
    const PACK: u32 = 0x5000_0001;
    const CARRIED: u32 = 0x7000_0702;
    type Statement = fn(&mut ClientObjectTable, &mut Vec<TableEvent>);
    let statements: [(&str, Statement); 16] = [
        ("create", |t, e| {
            t.ingest(
                &WeenieData {
                    guid: CARRIED,
                    name: Some("Gem".into()),
                    item_type: Some(ItemType::MISC),
                    weenie_class_id: 2,
                    value: Some(5),
                    stack_size: Some(1),
                    stack_size_max: Some(1),
                    burden: Some(10),
                    container_id: Some(PACK),
                    ..WeenieData::default()
                },
                e,
            );
        }),
        ("properties", |t, e| {
            t.update_properties(CARRIED, &PropertyBundle::default(), e);
        }),
        ("upsert-properties", |t, e| {
            t.upsert_properties(CARRIED, &PropertyBundle::default(), e);
        }),
        ("int-property", |t, e| {
            t.update_int_property(CARRIED, 19, 40, e);
        }),
        ("data-id-property", |t, e| {
            t.update_data_id_property(CARRIED, 8, 0x0600_1234, e);
        }),
        ("instance-id-property", |t, e| {
            t.update_instance_id_property(CARRIED, 3, 0x5000_0009, e);
        }),
        ("int64-property", |t, e| {
            t.update_int64_property(CARRIED, 1, 7, e);
        }),
        ("stack-size", |t, e| {
            t.update_stack_size(CARRIED, 3, 15, e);
        }),
        ("house-restrictions", |t, e| {
            t.update_house_restrictions(
                CARRIED,
                HouseRestrictionRecord {
                    open_to_public: true,
                    allegiance_monarch_id: 0,
                    guests: std::collections::BTreeMap::new(),
                },
                e,
            );
        }),
        ("server-move", |t, e| {
            t.apply_server_move(
                CARRIED,
                ClientObjectPlacement::in_container(PACK, 0),
                None,
                e,
            );
        }),
        ("confirmed-move", |t, e| {
            t.apply_confirmed_server_move(
                CARRIED,
                ClientObjectPlacement::in_container(PACK, 0),
                None,
                e,
            );
        }),
        ("confirmed-wield", |t, e| {
            t.apply_confirmed_server_wield(CARRIED, PACK, EquipMask::HEAD_WEAR, e);
        }),
        ("membership", |t, e| {
            t.record_membership(CARRIED, PACK, EquipMask::NONE, None, None, e);
        }),
        ("pack-listing", |t, e| {
            t.replace_contents(PACK, &[entry(CARRIED, 0)], e);
        }),
        ("inventory-manifest", |t, e| {
            t.initialize_inventory_manifest(PACK, &[entry(CARRIED, 0)], e);
        }),
        ("equipment-manifest", |t, e| {
            t.initialize_equipment_manifest(
                PACK,
                &[EquipmentManifestEntry {
                    guid: CARRIED,
                    equip_location: EquipMask::HEAD_WEAR,
                    priority: 0,
                }],
                e,
            );
        }),
    ];
    for (name, statement) in statements {
        let mut f = Fixture::new();
        let mut carried = ClientObject::new(CARRIED);
        carried.container_id = PACK;
        f.add(carried);
        assert!(f.t.record_unsuccessful_appraisal(CARRIED, 0.0, &mut f.ev));
        f.take();

        statement(&mut f.t, &mut f.ev);

        assert!(!f.get(CARRIED).last_appraisal_unsuccessful, "{name}");
        let events = f.take();
        let heard = events.iter().any(|event| match event {
            TableEvent::Updated { object_id, .. } | TableEvent::Added { object_id, .. } => {
                *object_id == CARRIED
            }
            TableEvent::Moved(m) => m.item_id == CARRIED,
            _ => false,
        });
        assert!(heard, "{name}: nothing was published.");
    }
}

// OpenAC `ClientObjectTableUpdateTests.AMoveTheClientMakesOnItsOwn_leavesARefusalStanding`:
// a move the client makes ahead of the server says nothing about whether
// the server still has the object.
#[test]
fn a_move_the_client_makes_on_its_own_leaves_a_refusal_standing() {
    const PACK: u32 = 0x5000_0001;
    const CARRIED: u32 = 0x7000_0702;
    let mut f = Fixture::new();
    let mut carried = ClientObject::new(CARRIED);
    carried.container_id = PACK;
    f.add(carried);
    assert!(f.t.record_unsuccessful_appraisal(CARRIED, 0.0, &mut f.ev));

    assert!(f.t.move_item_optimistic(CARRIED, 0x5000_0002, 0, &mut f.ev));

    assert!(f.get(CARRIED).last_appraisal_unsuccessful);
}

// --- A re-sent description re-creates the object -----------------------
//
// The game handles a re-sent description by deleting the object, which
// takes it out of its container's list, and creating it again; the
// container then has no place for it and puts it first among its own kind
// (items or packs). Revealing a coalesced Aetheria's sigil sends one, so
// the revealed Aetheria lands in its pack's top-left slot.

const RECREATE_PACK: u32 = 0x5000_00C9;

fn named_in(guid: u32, container: u32, name: &str) -> WeenieData {
    WeenieData {
        name: Some(name.into()),
        ..in_container(guid, container)
    }
}

#[test]
fn ingest_recreated_puts_the_item_first_in_its_pack() {
    let items = [0x7000_0A01, 0x7000_0A02, 0x7000_0A03];
    let mut f = Fixture::new();
    for (slot, &id) in items.iter().enumerate() {
        f.ingest(named_in(id, RECREATE_PACK, "Coalesced Aetheria"));
        assert!(f.server_move(id, RECREATE_PACK, slot as i32));
    }
    assert_eq!(f.contents(RECREATE_PACK), items);
    f.take();

    f.t.ingest_recreated(&named_in(items[2], RECREATE_PACK, "Aetheria"), &mut f.ev);

    assert_eq!(f.contents(RECREATE_PACK), [items[2], items[0], items[1]]);
    let slots: Vec<i32> = [items[2], items[0], items[1]]
        .iter()
        .map(|&id| f.get(id).container_slot)
        .collect();
    assert_eq!(slots, [0, 1, 2]);
    assert_eq!(f.get(items[2]).name, "Aetheria");
    let moved = moves(&f.take());
    assert_eq!(moved.len(), 1);
    assert_eq!(moved[0].item_id, items[2]);
    assert_eq!(moved[0].current.container_slot, 0);
}

#[test]
fn ingest_recreated_puts_a_pack_first_among_packs_and_leaves_the_items() {
    let (item_a, item_b) = (0x7000_0B01, 0x7000_0B02);
    let (pack_a, pack_b) = (0x7000_0B11, 0x7000_0B12);
    let mut f = Fixture::new();
    for (slot, &id) in [item_a, item_b].iter().enumerate() {
        f.ingest(in_container(id, PLAYER));
        assert!(f.server_move(id, PLAYER, slot as i32));
    }
    for (slot, &id) in [pack_a, pack_b].iter().enumerate() {
        f.ingest(in_container(id, PLAYER));
        assert!(f.confirmed_move(id, PLAYER, slot as i32, 1));
    }

    f.t.ingest_recreated(&in_container(pack_b, PLAYER), &mut f.ev);

    let order = f.contents(PLAYER);
    let packs: Vec<u32> = order
        .iter()
        .copied()
        .filter(|id| [pack_a, pack_b].contains(id))
        .collect();
    let loose: Vec<u32> = order
        .iter()
        .copied()
        .filter(|id| [item_a, item_b].contains(id))
        .collect();
    assert_eq!(packs, [pack_b, pack_a]);
    assert_eq!(loose, [item_a, item_b]);
    assert_eq!(f.get(pack_b).container_slot, 0);
    assert_eq!(f.get(pack_a).container_slot, 1);
    assert_eq!(f.get(item_a).container_slot, 0);
    assert_eq!(f.get(item_b).container_slot, 1);
}

#[test]
fn ingest_recreated_without_a_container_keeps_where_the_object_is() {
    const SWORD: u32 = 0x7000_0C01;
    let mut f = Fixture::new();
    f.ingest(full_weenie(SWORD));
    assert!(f.t.apply_server_move(
        SWORD,
        wielded(PLAYER, EquipMask::MELEE_WEAPON),
        None,
        &mut f.ev
    ));
    f.take();

    f.t.ingest_recreated(&full_weenie(SWORD), &mut f.ev);

    assert_eq!(f.get(SWORD).wielder_id, PLAYER);
    assert_eq!(
        f.get(SWORD).currently_equipped_location,
        EquipMask::MELEE_WEAPON
    );
    assert!(moves(&f.take()).is_empty());
}
